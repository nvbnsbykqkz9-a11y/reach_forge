using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Tests;

/// <summary>ナレーションの音声合成：モデルの決め方（空欄・文章のモデルを使わない）と、失敗の理由の伝え方。</summary>
public class TextToSpeechTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private sealed class ListSink : IAiUsageSink
    {
        public readonly List<AiUsageLog> Logs = [];
        public void Add(AiUsageLog log) => Logs.Add(log);
        public IReadOnlyList<AiUsageLog> Drain() => [];
    }

    private static readonly byte[] Wav = TextToSpeechService.SilentWav(1.5);

    private static (TextToSpeechService Service, Handler Handler, ListSink Usage) Create(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string model = "", Dictionary<string, string>? taskModels = null, ProviderCircuitBreaker? breaker = null)
    {
        var opts = Options.Create(new AiOptions
        {
            Providers =
            {
                ["openai"] = new AiProviderOptions
                {
                    Type = AiProviderType.OpenAI, ApiKey = "sk-test", Model = model, TaskModels = taskModels ?? [], PricePerSpeechMinute = 0.015m,
                },
            },
            Routes = { ["Tts"] = ["openai"] },
        });
        var handler = new Handler(respond);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var usage = new ListSink();
        return (new TextToSpeechService(opts, breaker ?? new ProviderCircuitBreaker(opts, TimeProvider.System), usage, http,
            NullLogger<TextToSpeechService>.Instance), handler, usage);
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string type = "invalid_request_error", string message = "") =>
        new(status)
        {
            Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = code } }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };

    [Fact]
    public void Blank_models_mean_not_set_and_speech_never_uses_the_text_model()
    {
        Assert.Null(new AiProviderOptions { Model = "" }.ModelFor(AiTaskType.Tts));
        Assert.Null(new AiProviderOptions { Model = "  ", TaskModels = { ["Copy"] = "" } }.ModelFor(AiTaskType.Copy));
        Assert.Equal("gpt-5", new AiProviderOptions { Model = "gpt-5", TaskModels = { ["Copy"] = " " } }.ModelFor(AiTaskType.Copy));
        Assert.Equal(AiProviderOptions.DefaultSpeechModel, new AiProviderOptions { Model = "gpt-5" }.SpeechModel);
        Assert.Equal("tts-1", new AiProviderOptions { Model = "gpt-5", TaskModels = { ["Tts"] = "tts-1" } }.SpeechModel);
    }

    [Theory]
    [InlineData("")]       // 以前はモデル名が空のまま送られて失敗していた
    [InlineData("gpt-5")]  // 文章のモデルを音声合成に使わない
    public async Task Speech_uses_the_speech_model_and_records_the_cost(string textModel)
    {
        var (tts, handler, usage) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav) }, textModel);
        var audio = await tts.SynthesizeAsync("秋限定のさつまいもラテ。", CancellationToken.None);

        Assert.Equal(1.5, audio.Seconds, 2);
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal(AiProviderOptions.DefaultSpeechModel, body["model"]!.GetValue<string>());
        Assert.Equal("wav", body["response_format"]!.GetValue<string>());
        Assert.Equal(AiProviderOptions.DefaultSpeechModel, usage.Logs.Single().ModelId);
        Assert.Equal(0.015m * 1.5m / 60m, usage.Logs.Single().CostUsd, 6);
    }

    public static TheoryData<HttpStatusCode, string, string, string> Failures => new()
    {
        { HttpStatusCode.Unauthorized, "invalid_api_key", "invalid_request_error", "API キーが正しくないか" },
        { HttpStatusCode.TooManyRequests, "insufficient_quota", "insufficient_quota", "残高（クレジット）が足りません" },
        { HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "requests", "少し時間をおいて" },
        { HttpStatusCode.NotFound, "model_not_found", "invalid_request_error", "音声合成のモデル「gpt-4o-mini-tts」を使えません" },
        { HttpStatusCode.InternalServerError, "server_error", "server_error", "OpenAI 側で障害" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failures_explain_what_to_do(HttpStatusCode status, string code, string type, string expected)
    {
        var (tts, _, _) = Create(_ => Error(status, code, type));
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => tts.SynthesizeAsync("こんにちは", CancellationToken.None));
        Assert.StartsWith("ナレーションを作れませんでした：", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Connection_failures_and_missing_keys_are_explained()
    {
        var (tts, _, _) = Create(_ => throw new HttpRequestException("No such host"));
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => tts.SynthesizeAsync("こんにちは", CancellationToken.None));
        Assert.Contains("OpenAI に接続できませんでした", ex.Message);

        var opts = Options.Create(new AiOptions
        {
            Providers = { ["openai"] = new AiProviderOptions { Type = AiProviderType.OpenAI } },
            Routes = { ["Tts"] = ["openai"] },
        });
        var noKey = new TextToSpeechService(opts, new ProviderCircuitBreaker(opts, TimeProvider.System), new ListSink(),
            Substitute.For<IHttpClientFactory>(), NullLogger<TextToSpeechService>.Instance);
        ex = await Assert.ThrowsAsync<AiUnavailableException>(() => noKey.SynthesizeAsync("こんにちは", CancellationToken.None));
        Assert.Contains("API キーが設定されていません", ex.Message);
    }

    [Fact]
    public async Task Failures_of_other_features_do_not_stop_narration()
    {
        var breaker = new ProviderCircuitBreaker(Options.Create(new AiOptions()), TimeProvider.System);
        // 同じ OpenAI の画像生成・文章が続けて失敗しても
        for (var i = 0; i < 10; i++)
        {
            breaker.RecordFailure(ProviderCircuitBreaker.Key("openai", ProviderCircuitBreaker.Image));
            breaker.RecordFailure("openai");
        }
        var (service, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav) }, breaker: breaker);

        var speech = await service.SynthesizeAsync("こんにちは", CancellationToken.None);
        Assert.True(speech.Seconds > 1);
    }

    [Fact]
    public async Task When_narration_is_paused_the_message_tells_the_cause()
    {
        var breaker = new ProviderCircuitBreaker(Options.Create(new AiOptions()), TimeProvider.System);
        var (service, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error","code":"invalid_api_key"}}""",
                Encoding.UTF8, "application/json"),
        }, breaker: breaker);
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<AiUnavailableException>(() => service.SynthesizeAsync("こんにちは", CancellationToken.None));
        }

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => service.SynthesizeAsync("こんにちは", CancellationToken.None));
        Assert.Contains("1分ほど止めています", ex.Message);
        Assert.Contains("API キー", ex.Message); // 止めたきっかけ（キーが正しくない）を添える
    }
}
