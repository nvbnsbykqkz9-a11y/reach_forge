using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;

namespace ReachForge.AI.Providers;

/// <summary>動画生成モデル（Microsoft.Extensions.AI に動画の抽象がないため独自に定義：RF-DES-001 4.3）。</summary>
public interface IVideoGenerator
{
    /// <summary>MP4 を返す。完了まで待つ（長い処理はジョブとして呼ぶ）。</summary>
    Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct);
}

public interface IVideoGeneratorFactory
{
    IVideoGenerator Get(string providerName, AiProviderOptions options);
}

public sealed class VideoGeneratorFactory(IServiceProvider services, IHttpClientFactory http) : IVideoGeneratorFactory
{
    public const string HttpClientName = "ai-video";

    /// <summary>生成の完了を確かめる間隔（テストでは短くする）。</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public IVideoGenerator Get(string providerName, AiProviderOptions options) => options.Type switch
    {
        AiProviderType.Stub => new StubVideoGenerator(services.GetRequiredService<IImageProcessor>(), services.GetRequiredService<IVideoComposer>()),
        AiProviderType.OpenAI => new SoraVideoGenerator(http.CreateClient(HttpClientName), options),
        AiProviderType.Google => new VeoVideoGenerator(http.CreateClient(HttpClientName), options),
        AiProviderType.Kling => new KlingVideoGenerator(http.CreateClient(HttpClientName), options),
        _ => throw new NotSupportedException($"{options.Type} does not support video generation."),
    };
}

/// <summary>ローカル用：起点の画像（なければプロンプトから作る図形）に、ゆっくりズームをかけた動画。</summary>
public sealed class StubVideoGenerator(IImageProcessor images, IVideoComposer composer) : IVideoGenerator
{
    public async Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
    {
        var frame = spec.StartImage
            ?? (await images.RenderPlaceholderAsync(spec.Size.Width, spec.Size.Height, spec.Prompt.GetHashCode(StringComparison.Ordinal), [], ct)).Bytes;
        var jpeg = await images.EncodeJpegAsync(frame, 5_000_000, ct);
        return (await composer.ComposeAsync([new VideoSceneInput(jpeg.Bytes, spec.Seconds, null)], ct)).Mp4;
    }
}

/// <summary>
/// OpenAI Sora（Videos API）。依頼 → 状態の確認（queued / in_progress → completed / failed）→ 動画の取得。
/// 長さは 4・8・12 秒、縦型は 720×1280（後段で 1080×1920 に整える）。② は起点の画像を input_reference で渡す。
/// </summary>
public sealed class SoraVideoGenerator(HttpClient http, AiProviderOptions options) : IVideoGenerator
{
    public async Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
    {
        var baseUrl = (options.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        using var form = new MultipartFormDataContent
        {
            { new StringContent(string.IsNullOrEmpty(modelId) ? "sora-2" : modelId), "model" },
            { new StringContent(spec.Prompt), "prompt" },
            { new StringContent(Seconds(spec.Seconds).ToString(System.Globalization.CultureInfo.InvariantCulture)), "seconds" },
            { new StringContent(Landscape(spec) ? "1280x720" : "720x1280"), "size" },
        };
        if (spec.StartImage is { } image)
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue(spec.StartImageMime ?? "image/jpeg");
            form.Add(file, "input_reference", spec.StartImageMime == "image/png" ? "start.png" : "start.jpg");
        }
        var created = await SendAsync<JsonObject>(HttpMethod.Post, $"{baseUrl}/videos", form, ct);
        var id = created["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Sora did not return an id");

        while (true)
        {
            await Task.Delay(VideoGeneratorFactory.PollInterval, ct);
            var status = await SendAsync<JsonObject>(HttpMethod.Get, $"{baseUrl}/videos/{id}", null, ct);
            switch (status["status"]?.GetValue<string>())
            {
                case "completed":
                    using (var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/videos/{id}/content"))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                        using var response = await http.SendAsync(request, ct);
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsByteArrayAsync(ct);
                    }
                case "failed":
                    throw new InvalidOperationException($"Sora failed: {status["error"]?.ToJsonString()}");
            }
        }
    }

    /// <summary>横型（16:9）でつくるか。それ以外は縦型（9:16）。起点の画像も同じ向き・大きさ（720×1280 / 1280×720）で渡すこと。</summary>
    public static bool Landscape(VideoGenerationSpec spec) => spec.Size.Width > spec.Size.Height;

    /// <summary>Sora が受け付ける長さ（4・8・12 秒）のうち近いもの。</summary>
    public static int Seconds(int requested) => requested <= 6 ? 4 : requested <= 10 ? 8 : 12;

    private async Task<T> SendAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Sora {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}

/// <summary>動画生成 AI に共通で渡す「描かないもの」（文字・ロゴは崩れやすく、人物の顔は肖像権のため）。</summary>
internal static class VideoPrompts
{
    public const string Negative = VideoGenerationSpec.DefaultNegativePrompt;
}

/// <summary>
/// Google Veo（Gemini API の predictLongRunning）。依頼 → 操作（operation）の完了を確認 → 動画の URI から取得。
/// 既定は Veo 3.1 Fast。縦型（9:16）または横型（16:9）を 720p で、長さは 4・6・8 秒。② は起点の画像を image として渡す。
/// </summary>
public sealed class VeoVideoGenerator(HttpClient http, AiProviderOptions options) : IVideoGenerator
{
    public const string DefaultModel = "veo-3.1-fast-generate-preview";

    public async Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
    {
        var baseUrl = (options.BaseUrl ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        // 画像のモデル（gemini-…-image）が指定されていても、動画には Veo を使う
        var model = string.IsNullOrEmpty(modelId) || !modelId.StartsWith("veo", StringComparison.OrdinalIgnoreCase) ? DefaultModel : modelId;
        var instance = new JsonObject { ["prompt"] = spec.Prompt };
        if (spec.StartImage is { } image)
        {
            instance["image"] = new JsonObject
            {
                ["bytesBase64Encoded"] = Convert.ToBase64String(image),
                ["mimeType"] = spec.StartImageMime ?? "image/jpeg",
            };
        }
        var body = new JsonObject
        {
            ["instances"] = new JsonArray { instance },
            ["parameters"] = new JsonObject
            {
                ["aspectRatio"] = SoraVideoGenerator.Landscape(spec) ? "16:9" : "9:16",
                ["durationSeconds"] = Seconds(spec.Seconds),
                ["resolution"] = "720p", // 縦型でも使える大きさ（書き出し時に 1080 に整える）
                ["negativePrompt"] = spec.NegativePrompt is { Length: > 0 } negative ? negative : VideoPrompts.Negative,
            },
        };
        var operation = await SendAsync(HttpMethod.Post, $"{baseUrl}/models/{model}:predictLongRunning", body, ct);
        var name = operation["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Veo did not return an operation");

        while (operation["done"]?.GetValue<bool>() != true)
        {
            await Task.Delay(VideoGeneratorFactory.PollInterval, ct);
            operation = await SendAsync(HttpMethod.Get, $"{baseUrl}/{name}", null, ct);
        }
        if (operation["error"] is { } error) throw new InvalidOperationException($"Veo failed: {error.ToJsonString()}");
        var uri = operation["response"]?["generateVideoResponse"]?["generatedSamples"]?[0]?["video"]?["uri"]?.GetValue<string>()
                  ?? throw new InvalidOperationException("Veo returned no video (it may have been filtered by safety settings).");
        using var download = new HttpRequestMessage(HttpMethod.Get, uri);
        download.Headers.Add("x-goog-api-key", options.ApiKey);
        using var response = await http.SendAsync(download, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Veo 3.x が受け付ける長さ（4・6・8 秒）のうち、指定以上で近いもの。</summary>
    public static int Seconds(int requested) => requested <= 4 ? 4 : requested <= 6 ? 6 : 8;

    private async Task<JsonObject> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Veo {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<JsonObject>(ct))!;
    }
}

/// <summary>
/// Kling（Kuaishou）。依頼（画像→動画 image2video、文章→動画 text2video）→ タスクの状態の確認（submitted / processing → succeed / failed）→ 動画の URL から取得。
/// 既定は kling-v3 の標準モード（720p）。長さは 5 秒または 10 秒、縦型（9:16）・横型（16:9）。
/// 認証は API キー、またはアクセスキー（<see cref="AiProviderOptions.ApiKey"/>）とシークレットキーから作るトークン（JWT・HS256、30分）。
/// </summary>
public sealed class KlingVideoGenerator(HttpClient http, AiProviderOptions options) : IVideoGenerator
{
    public const string DefaultModel = "kling-v3";
    public const string DefaultBaseUrl = "https://api-singapore.klingai.com";

    public async Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
    {
        var baseUrl = BaseUrl(options);
        var kind = spec.StartImage is null ? "text2video" : "image2video";
        var body = new JsonObject
        {
            ["model_name"] = string.IsNullOrEmpty(modelId) ? DefaultModel : modelId,
            ["prompt"] = spec.Prompt.Length > 2500 ? spec.Prompt[..2500] : spec.Prompt,
            ["negative_prompt"] = spec.NegativePrompt is { Length: > 0 } negative ? negative : VideoPrompts.Negative,
            ["mode"] = spec.HighQuality ? "pro" : "std",
            ["duration"] = Seconds(spec.Seconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (spec.StartImage is { } image)
        {
            // 起点の画像の比率（9:16・16:9 に整えたもの）がそのまま動画の比率になる
            body["image"] = Convert.ToBase64String(image);
        }
        else
        {
            body["aspect_ratio"] = SoraVideoGenerator.Landscape(spec) ? "16:9" : "9:16";
        }
        var created = await SendAsync(HttpMethod.Post, $"{baseUrl}/v1/videos/{kind}", body, ct);
        var id = created["data"]?["task_id"]?.GetValue<string>() ?? throw new InvalidOperationException("Kling did not return a task id");

        while (true)
        {
            await Task.Delay(VideoGeneratorFactory.PollInterval, ct);
            var status = await SendAsync(HttpMethod.Get, $"{baseUrl}/v1/videos/{kind}/{id}", null, ct);
            var data = status["data"];
            switch (data?["task_status"]?.GetValue<string>())
            {
                case "succeed":
                    var url = data["task_result"]?["videos"]?[0]?["url"]?.GetValue<string>()
                              ?? throw new InvalidOperationException("Kling returned no video");
                    using (var response = await http.GetAsync(url, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsByteArrayAsync(ct);
                    }
                case "failed":
                    throw new InvalidOperationException($"Kling failed: {data["task_status_msg"]?.GetValue<string>()}");
            }
        }
    }

    public static string BaseUrl(AiProviderOptions options) => (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');

    /// <summary>Kling が受け付ける長さ（5・10 秒）のうち近いもの。</summary>
    public static int Seconds(int requested) => requested <= 7 ? 5 : 10;

    /// <summary>
    /// 認証のトークン。シークレットキーがあれば、アクセスキーを発行者（iss）として HS256 で署名した JWT（30分有効、時計のずれに5秒の余裕）。
    /// なければ API キーをそのまま使う。
    /// </summary>
    public static string Token(AiProviderOptions options, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(options.SecretKey)) return options.ApiKey ?? "";
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(System.Text.Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = options.ApiKey?.Trim(),
            exp = now.AddMinutes(30).ToUnixTimeSeconds(),
            nbf = now.AddSeconds(-5).ToUnixTimeSeconds(),
        }));
        var signature = System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(options.SecretKey.Trim()),
            System.Text.Encoding.UTF8.GetBytes($"{header}.{payload}"));
        return $"{header}.{payload}.{B64(signature)}";
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(options, DateTimeOffset.UtcNow));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Kling {(int)response.StatusCode}: {json}", null, response.StatusCode);
        }
        var result = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Kling returned an invalid response");
        // HTTP 200 でも code が 0 以外ならエラー（残高不足・内容の制限など）
        if (result["code"]?.GetValue<int>() is { } code and not 0)
        {
            throw new HttpRequestException($"Kling error {code}: {result["message"]?.GetValue<string>()}");
        }
        return result;
    }
}
