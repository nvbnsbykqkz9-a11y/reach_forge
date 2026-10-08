using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;

namespace ReachForge.AI.Tests;

/// <summary>生成 AI の動画（F-05 ①②）：Sora・Veo・Kling の API 呼び出し、代替プロバイダ、15分の上限。</summary>
public class VideoGenerationTests
{
    /// <summary>要求を記録し、URL に応じて応答する HTTP ハンドラ。</summary>
    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var auth = request.Headers.Authorization?.ToString() ?? request.Headers.FirstOrDefault(h => h.Key == "x-goog-api-key").Value?.FirstOrDefault();
            Requests.Add((request.Method, request.RequestUri!.ToString(), body, auth));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'];

    public VideoGenerationTests() => VideoGeneratorFactory.PollInterval = TimeSpan.Zero;

    [Fact]
    public async Task Sora_creates_polls_and_downloads_with_the_start_image()
    {
        var polls = 0;
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/v1/videos" => Json("""{"id":"video_1","status":"queued"}"""),
            "/v1/videos/video_1" => Json(++polls < 2 ? """{"id":"video_1","status":"in_progress","progress":40}""" : """{"id":"video_1","status":"completed"}"""),
            "/v1/videos/video_1/content" => Bytes(Mp4),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var sora = new SoraVideoGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "sk-test" });
        var mp4 = await sora.GenerateAsync(new VideoGenerationSpec { Prompt = "湯気の立つラテ", Seconds = 10, StartImage = [1, 2, 3], StartImageMime = "image/jpeg" },
            "sora-2", CancellationToken.None);

        Assert.Equal(Mp4, mp4);
        var create = handler.Requests[0];
        Assert.Equal("Bearer sk-test", create.Auth);
        Assert.Contains("name=model", create.Body);
        Assert.Contains("sora-2", create.Body);
        Assert.Contains("name=seconds", create.Body);
        Assert.Contains("\r\n\r\n8\r\n", create.Body);           // 10秒 → 対応する 8秒
        Assert.Contains("720x1280", create.Body);                 // 縦型
        Assert.Contains("name=input_reference", create.Body);     // ② 画像→動画
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Veo_starts_a_long_running_operation_and_downloads_the_result()
    {
        var done = false;
        var handler = new Handler((r, _) => r.RequestUri!.ToString() switch
        {
            var u when u.EndsWith(":predictLongRunning", StringComparison.Ordinal) => Json("""{"name":"models/veo-3.0-generate-001/operations/op1"}"""),
            var u when u.EndsWith("/operations/op1", StringComparison.Ordinal) => Json((done = !done) ? """{"name":"op1","done":false}"""
                : """{"name":"op1","done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://files.example/v.mp4"}}]}}}"""),
            "https://files.example/v.mp4" => Bytes(Mp4),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var veo = new VeoVideoGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Google, ApiKey = "g-key" });
        var mp4 = await veo.GenerateAsync(new VideoGenerationSpec { Prompt = "秋の公園", Seconds = 12 }, "", CancellationToken.None);

        Assert.Equal(Mp4, mp4);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("9:16", body["parameters"]!["aspectRatio"]!.GetValue<string>());
        Assert.Equal(8, body["parameters"]!["durationSeconds"]!.GetValue<int>());
        Assert.Equal("720p", body["parameters"]!["resolution"]!.GetValue<string>());
        Assert.Null(body["parameters"]!["personGeneration"]); // Veo 3.x では受け付けられない値があるため送らない
        Assert.Contains("text", body["parameters"]!["negativePrompt"]!.GetValue<string>());
        Assert.All(handler.Requests, r => Assert.Equal("g-key", r.Auth));
        Assert.Contains(VeoVideoGenerator.DefaultModel, handler.Requests[0].Url);
    }

    [Theory]
    [InlineData("", VeoVideoGenerator.DefaultModel)]
    [InlineData("gemini-3-pro-image", VeoVideoGenerator.DefaultModel)] // 画像のモデルが指定されていても動画には Veo
    [InlineData("veo-3.1-generate-preview", "veo-3.1-generate-preview")]
    public async Task Veo_uses_a_video_model(string configured, string expected)
    {
        var handler = new Handler((r, _) => r.RequestUri!.ToString() switch
        {
            var u when u.EndsWith(":predictLongRunning", StringComparison.Ordinal) => Json(
                """{"name":"op1","done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://files.example/v.mp4"}}]}}}"""),
            _ => Bytes(Mp4),
        });
        var veo = new VeoVideoGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Google, ApiKey = "g-key" });
        await veo.GenerateAsync(new VideoGenerationSpec { Prompt = "x", Seconds = 5, Size = (1920, 1080) }, configured, CancellationToken.None);

        Assert.Contains($"/models/{expected}:predictLongRunning", handler.Requests[0].Url);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("16:9", body["parameters"]!["aspectRatio"]!.GetValue<string>());
        Assert.Equal(6, body["parameters"]!["durationSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Kling_creates_an_image_to_video_task_polls_and_downloads()
    {
        var polls = 0;
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/v1/videos/image2video" => Json("""{"code":0,"message":"SUCCEED","data":{"task_id":"t1","task_status":"submitted"}}"""),
            "/v1/videos/image2video/t1" => Json(++polls < 2
                ? """{"code":0,"data":{"task_id":"t1","task_status":"processing"}}"""
                : """{"code":0,"data":{"task_id":"t1","task_status":"succeed","task_result":{"videos":[{"id":"v1","url":"https://cdn.example/v.mp4","duration":"5"}]}}}"""),
            "/v.mp4" => Bytes(Mp4),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var kling = new KlingVideoGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Kling, ApiKey = "k-key" });
        var mp4 = await kling.GenerateAsync(new VideoGenerationSpec { Prompt = "湯気の立つラテ", Seconds = 6, StartImage = [1, 2, 3], StartImageMime = "image/jpeg" },
            "", CancellationToken.None);

        Assert.Equal(Mp4, mp4);
        Assert.StartsWith(KlingVideoGenerator.DefaultBaseUrl, handler.Requests[0].Url);
        Assert.Equal("Bearer k-key", handler.Requests[0].Auth); // API キーはそのまま
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(KlingVideoGenerator.DefaultModel, body["model_name"]!.GetValue<string>());
        Assert.Equal("5", body["duration"]!.GetValue<string>());     // 6秒 → 対応する 5秒
        Assert.Equal("AQID", body["image"]!.GetValue<string>());     // 起点の画像（Base64）
        Assert.Null(body["aspect_ratio"]);                           // 比率は起点の画像に合わせる
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Kling_text_to_video_sets_the_aspect_ratio_and_reports_errors()
    {
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/v1/videos/text2video" => Json("""{"code":1102,"message":"Account balance not enough"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var kling = new KlingVideoGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Kling, ApiKey = "k" });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            kling.GenerateAsync(new VideoGenerationSpec { Prompt = "x", Seconds = 8, Size = (1920, 1080) }, "kling-v3", CancellationToken.None));

        Assert.Contains("balance", ex.Message);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("16:9", body["aspect_ratio"]!.GetValue<string>());
        Assert.Equal("10", body["duration"]!.GetValue<string>());
    }

    [Fact]
    public void Kling_signs_a_token_with_the_access_and_secret_keys()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var token = KlingVideoGenerator.Token(new AiProviderOptions { Type = AiProviderType.Kling, ApiKey = "ak", SecretKey = "sk" }, now);
        var parts = token.Split('.');

        Assert.Equal(3, parts.Length);
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))))!;
        Assert.Equal("ak", payload["iss"]!.GetValue<string>());
        Assert.Equal(1_800_001_800, payload["exp"]!.GetValue<long>());
        Assert.Equal(1_799_999_995, payload["nbf"]!.GetValue<long>());
        var expected = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("sk"), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal(expected, Convert.FromBase64String(Pad(parts[2])));

        static string Pad(string b64url)
        {
            var s = b64url.Replace('-', '+').Replace('_', '/');
            return s + new string('=', (4 - s.Length % 4) % 4);
        }
    }

    private sealed class FakeFactory(Dictionary<string, IVideoGenerator> generators) : IVideoGeneratorFactory
    {
        public IVideoGenerator Get(string providerName, AiProviderOptions options) => generators[providerName];
    }

    private sealed class Generator(Func<CancellationToken, Task<byte[]>> run) : IVideoGenerator
    {
        public int Calls;

        public Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
        {
            Calls++;
            return run(ct);
        }
    }

    private static VideoGenerationService Service(IVideoGeneratorFactory factory, List<AiUsageLog> usage) =>
        new(factory, new ProviderCircuitBreaker(Options.Create(new AiOptions()), TimeProvider.System), new ListSink(usage), new MutableTenantContext(),
            Options.Create(new AiOptions
            {
                Providers =
                {
                    ["google"] = new AiProviderOptions { Type = AiProviderType.Google, ApiKey = "g", PricePerVideoSecond = 0.4m },
                    ["openai"] = new AiProviderOptions { Type = AiProviderType.OpenAI, ApiKey = "o", PricePerVideoSecond = 0.1m },
                },
                Routes = { ["VideoGeneration"] = ["google", "openai"] },
            }), NullLogger<VideoGenerationService>.Instance);

    private sealed class ListSink(List<AiUsageLog> logs) : IAiUsageSink
    {
        public void Add(AiUsageLog log) => logs.Add(log);
        public IReadOnlyList<AiUsageLog> Drain() => logs;
    }

    [Fact]
    public async Task Falls_back_to_the_next_provider_and_records_cost_per_second()
    {
        var usage = new List<AiUsageLog>();
        var google = new Generator(_ => throw new HttpRequestException("503"));
        var openai = new Generator(_ => Task.FromResult(Mp4));
        var result = await Service(new FakeFactory(new() { ["google"] = google, ["openai"] = openai }), usage)
            .GenerateAsync(new VideoGenerationSpec { Prompt = "x", Seconds = 8 }, null, CancellationToken.None);

        Assert.Equal(("openai", true), (result.Model.Provider, result.Model.FallbackUsed));
        Assert.Equal([false, true], usage.Select(u => u.Succeeded));
        Assert.Equal(0.8m, usage[1].CostUsd);
    }

    [Fact]
    public async Task Gives_up_after_the_time_limit_and_suggests_the_template()
    {
        var previous = VideoGenerationService.Timeout;
        VideoGenerationService.Timeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var slow = new Generator(async ct => { await Task.Delay(Timeout.Infinite, ct); return Mp4; });
            var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => Service(new FakeFactory(new() { ["google"] = slow, ["openai"] = slow }), [])
                .GenerateAsync(new VideoGenerationSpec { Prompt = "x" }, null, CancellationToken.None));
            Assert.Contains("テンプレート合成", ex.Message);
            Assert.Equal(1, slow.Calls); // 上限に達したら代替プロバイダでやり直さない
        }
        finally
        {
            VideoGenerationService.Timeout = previous;
        }
    }
}
