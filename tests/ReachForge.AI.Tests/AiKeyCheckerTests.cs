using System.Net;
using Microsoft.Extensions.Options;

namespace ReachForge.AI.Tests;

/// <summary>生成 AI の API キーの確認（モデル一覧の取得。生成はしない）。</summary>
public class AiKeyCheckerTests
{
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Last { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private static AiKeyChecker Checker(Handler handler, string name, AiProviderType type, string? key) =>
        new(handler, Options.Create(new AiOptions { Providers = { [name] = new AiProviderOptions { Type = type, ApiKey = key } } }));

    [Theory]
    [InlineData("anthropic", AiProviderType.Anthropic, "https://api.anthropic.com/v1/models", "x-api-key")]
    [InlineData("google", AiProviderType.Google, "https://generativelanguage.googleapis.com/v1beta/models", "x-goog-api-key")]
    public async Task Valid_key_is_confirmed_with_the_provider_header(string name, AiProviderType type, string url, string header)
    {
        var handler = new Handler(HttpStatusCode.OK);
        var result = await Checker(handler, name, type, "k-123").CheckAsync(name, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.StartsWith(url, handler.Last!.RequestUri!.ToString());
        Assert.Equal("k-123", handler.Last.Headers.GetValues(header).Single());
    }

    [Fact]
    public async Task OpenAI_uses_bearer_and_rejected_key_is_reported()
    {
        var handler = new Handler(HttpStatusCode.Unauthorized);
        var result = await Checker(handler, "openai", AiProviderType.OpenAI, "sk-bad").CheckAsync("openai", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("正しくない", result.Message);
        Assert.Equal("Bearer sk-bad", handler.Last!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Missing_key_is_reported_without_calling_the_provider()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var result = await Checker(handler, "openai", AiProviderType.OpenAI, null).CheckAsync("openai", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Null(handler.Last);
    }

    [Fact]
    public void Chat_clients_are_rebuilt_when_the_key_changes()
    {
        using var factory = new Providers.ChatClientFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var first = factory.Get("openai", new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "sk-1" }, "gpt");
        Assert.Same(first, factory.Get("openai", new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "sk-1" }, "gpt"));
        Assert.NotSame(first, factory.Get("openai", new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "sk-2" }, "gpt"));
    }
}
