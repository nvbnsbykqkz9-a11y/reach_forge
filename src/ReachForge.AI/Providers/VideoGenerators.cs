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
            { new StringContent("720x1280"), "size" },
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

/// <summary>
/// Google Veo（Gemini API の predictLongRunning）。依頼 → 操作（operation）の完了を確認 → 動画の URI から取得。
/// 縦型（9:16）、長さは 4〜8 秒。② は起点の画像を image として渡す。
/// </summary>
public sealed class VeoVideoGenerator(HttpClient http, AiProviderOptions options) : IVideoGenerator
{
    public async Task<byte[]> GenerateAsync(VideoGenerationSpec spec, string modelId, CancellationToken ct)
    {
        var baseUrl = (options.BaseUrl ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        var model = string.IsNullOrEmpty(modelId) ? "veo-3.0-generate-001" : modelId;
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
                ["aspectRatio"] = "9:16",
                ["durationSeconds"] = Math.Clamp(spec.Seconds, 4, 8),
                ["personGeneration"] = "dont_allow", // 実在の人物を生成しない（肖像権）
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
