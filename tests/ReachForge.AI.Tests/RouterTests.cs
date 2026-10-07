using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ReachForge.AI.Prompts;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Tests;

public class RouterTests
{
    private sealed class FailingClient : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("503");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new HttpRequestException("503");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SequenceClient(params string[] replies) : IChatClient
    {
        private int _i;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Math.Min(_i++, replies.Length - 1)])));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly AiOptions Options = new()
    {
        Providers =
        {
            ["primary"] = new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "k", Model = "m1", InputPricePerMTok = 1, OutputPricePerMTok = 2 },
            ["backup"] = new AiProviderOptions { Type = AiProviderType.Stub },
            ["unconfigured"] = new AiProviderOptions { Type = AiProviderType.Anthropic },
        },
        Routes = { ["Default"] = ["unconfigured", "primary", "backup"] },
    };

    private static (IModelRouter Router, FailingClient Failing, AiUsageCollector Usage, FakeTimeProvider Clock) Build()
    {
        var failing = new FailingClient();
        var factory = Substitute.For<IChatClientFactory>();
        factory.Get("primary", Arg.Any<AiProviderOptions>(), Arg.Any<string>()).Returns(failing);
        factory.Get("backup", Arg.Any<AiProviderOptions>(), Arg.Any<string>()).Returns(new StubChatClient());
        var clock = new FakeTimeProvider();
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        var usage = new AiUsageCollector();
        var router = new ConfigDrivenModelRouter(factory, new ProviderCircuitBreaker(opts, clock), usage,
            new MutableTenantContext(), opts, NullLogger<ConfigDrivenModelRouter>.Instance);
        return (router, failing, usage, clock);
    }

    private static ChatOptions DigestOptions() =>
        new AiCallContext(AiTaskType.Summarize, null, new AltStubPayload("秋限定ラテ")).Apply();

    [Fact]
    public async Task Fails_over_to_next_provider_and_records_usage()
    {
        var (router, failing, usage, _) = Build();

        var response = await router.Resolve(AiTaskType.Summarize).GetResponseAsync([new(ChatRole.User, "x")], DigestOptions());

        Assert.Equal(1, failing.Calls);
        Assert.Equal("backup", response.AdditionalProperties!["rf.provider"]);
        Assert.Equal(true, response.AdditionalProperties["rf.fallback"]);
        var logs = usage.Drain();
        Assert.Equal(2, logs.Count);
        Assert.False(logs[0].Succeeded);
        Assert.True(logs[1].Succeeded);
        Assert.True(logs[1].FallbackUsed);
    }

    [Fact]
    public async Task Circuit_opens_after_five_failures_and_closes_after_sixty_seconds()
    {
        var (router, failing, _, clock) = Build();
        for (var i = 0; i < 5; i++)
        {
            await router.Resolve(AiTaskType.Summarize).GetResponseAsync([new(ChatRole.User, "x")], DigestOptions());
        }
        Assert.Equal(5, failing.Calls);

        await router.Resolve(AiTaskType.Summarize).GetResponseAsync([new(ChatRole.User, "x")], DigestOptions());
        Assert.Equal(5, failing.Calls); // 遮断中は呼ばない

        clock.Advance(TimeSpan.FromSeconds(61));
        await router.Resolve(AiTaskType.Summarize).GetResponseAsync([new(ChatRole.User, "x")], DigestOptions());
        Assert.Equal(6, failing.Calls);
    }

    [Fact]
    public async Task Structured_output_is_regenerated_once_on_parse_failure()
    {
        var client = new SequenceClient("not json", """{"message":"OK"}""");
        var (value, _) = await CopyGenerationService.GetStructuredAsync<DigestResult>(client, [new(ChatRole.User, "x")],
            new ChatOptions(), CancellationToken.None);
        Assert.Equal("OK", value.Message);

        var broken = new SequenceClient("not json");
        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            CopyGenerationService.GetStructuredAsync<DigestResult>(broken, [new(ChatRole.User, "x")], new ChatOptions(),
                CancellationToken.None));
    }

    [Fact]
    public void No_configured_provider_raises_E_AI_001()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Providers = { ["a"] = new AiProviderOptions { Type = AiProviderType.OpenAI } },
            Routes = { ["Default"] = ["a"] },
        });
        var router = new ConfigDrivenModelRouter(Substitute.For<IChatClientFactory>(),
            new ProviderCircuitBreaker(opts, TimeProvider.System), new AiUsageCollector(), new MutableTenantContext(), opts,
            NullLogger<ConfigDrivenModelRouter>.Instance);
        Assert.Equal("E-AI-001", Assert.Throws<AiUnavailableException>(() => router.Resolve(AiTaskType.Copy)).ErrorCode);
    }
}

public class LpCreativeWriterTests
{
    private static LpCreativeWriter Writer()
    {
        var router = Substitute.For<IModelRouter>();
        router.Resolve(Arg.Any<AiTaskType>()).Returns(new StubChatClient());
        return new LpCreativeWriter(router, PromptTests.Catalog());
    }

    private static readonly WebPage Page = new(new Uri("https://example.com/latte"), "秋限定さつまいもラテ | ほっこりカフェ",
        "北海道産さつまいもを使った、ほっくり甘いラテ。10月末までの期間限定です。", "秋限定さつまいもラテ 680円。", [], []);

    private static readonly BrandContext Brand = new(new BrandProfile { BrandName = "ほっこりカフェ", NgWords = ["激安"] }, []);

    [Theory]
    [InlineData(SocialPlatform.Instagram)]
    [InlineData(SocialPlatform.X)]
    [InlineData(SocialPlatform.Facebook)]
    [InlineData(SocialPlatform.Threads)]
    [InlineData(SocialPlatform.TikTok)]
    [InlineData(SocialPlatform.YouTube)]
    [InlineData(SocialPlatform.Line)]
    public async Task Copies_fit_each_platforms_limits(SocialPlatform platform)
    {
        var result = await Writer().WriteAsync(Brand, Page, platform, CancellationToken.None);
        var limits = AdCopyLimits.For(platform);
        var c = PlatformCatalog.Get(platform);
        Assert.Equal(3, result.AdCopies.Count);
        Assert.All(result.AdCopies, x =>
        {
            Assert.InRange(PostText.Length(x.PrimaryText), 1, limits.PrimaryText);
            Assert.InRange(PostText.Length(x.Headline), 0, limits.Headline);
            Assert.InRange(PostText.Length(x.Description), 0, limits.Description);
            Assert.False(PostText.ContainsUrl(x.PrimaryText));
        });
        Assert.InRange(PostText.Length(PostText.Compose(result.PostText, result.Hashtags)), 1, c.MaxBodyLength);
        Assert.InRange(result.Hashtags.Count, 0, c.MaxHashtags ?? 30);
        Assert.False(result.Guardrail.HasErrors, string.Join(", ", result.Guardrail.Findings.Select(f => f.Message)));
    }

    [Fact]
    public async Task Line_has_no_hashtags_and_tiktok_has_no_headline()
    {
        Assert.Empty((await Writer().WriteAsync(Brand, Page, SocialPlatform.Line, CancellationToken.None)).Hashtags);
        Assert.All((await Writer().WriteAsync(Brand, Page, SocialPlatform.TikTok, CancellationToken.None)).AdCopies, x => Assert.Empty(x.Headline));
    }
}
