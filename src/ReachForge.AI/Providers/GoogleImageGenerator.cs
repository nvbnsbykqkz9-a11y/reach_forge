using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ReachForge.AI.Providers;

/// <summary>
/// Google の画像生成（Gemini API の generateContent、既定は Nano Banana Pro：gemini-3-pro-image）。
/// SNS の比率（1:1・4:5・9:16・16:9 など）をそのまま指定でき、参照画像（LP の商品写真など）を一緒に渡せる。
/// 依頼の大きさ（<see cref="ImageGenerationOptions.ImageSize"/>）に近い比率を選び、2K（長辺 約2000px）でつくる。
/// </summary>
public sealed class GoogleImageGenerator(HttpClient http, AiProviderOptions options, string modelId) : IImageGenerator
{
    public const string DefaultModel = "gemini-3-pro-image";

    /// <summary>Gemini の画像生成が受け付ける比率。</summary>
    public static readonly IReadOnlyList<(string Name, double Ratio)> Ratios =
    [
        ("1:1", 1.0), ("2:3", 2.0 / 3), ("3:2", 1.5), ("3:4", 0.75), ("4:3", 4.0 / 3), ("4:5", 0.8), ("5:4", 1.25),
        ("9:16", 9.0 / 16), ("16:9", 16.0 / 9), ("21:9", 21.0 / 9),
    ];

    /// <summary>幅÷高さにいちばん近い比率（比の対数の差で比べる）。</summary>
    public static string NearestRatio(double ratio) =>
        Ratios.MinBy(r => Math.Abs(Math.Log(r.Ratio) - Math.Log(Math.Max(0.01, ratio)))).Name;

    /// <summary>モデル名。動画のモデル（veo-…）や空欄が渡されたら、画像の既定のモデルを使う。</summary>
    public static string Model(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) || !modelId.Contains("image", StringComparison.OrdinalIgnoreCase) ? DefaultModel : modelId.Trim();

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? generation = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = (options.BaseUrl ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        var size = generation?.ImageSize ?? new System.Drawing.Size(1024, 1024);
        var parts = new JsonArray { new JsonObject { ["text"] = request.Prompt ?? "" } };
        foreach (var image in request.OriginalImages?.OfType<DataContent>() ?? [])
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject { ["mime_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data.Span) },
            });
        }
        var body = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["parts"] = parts } },
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "IMAGE" },
                ["imageConfig"] = new JsonObject
                {
                    ["aspectRatio"] = NearestRatio((double)size.Width / Math.Max(1, size.Height)),
                    ["imageSize"] = "2K",
                },
            },
        };

        var contents = new List<AIContent>();
        for (var i = 0; i < Math.Max(1, generation?.Count ?? 1); i++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/models/{Model(modelId)}:generateContent")
            {
                Content = JsonContent.Create(body),
            };
            message.Headers.Add("x-goog-api-key", options.ApiKey);
            using var response = await http.SendAsync(message, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Gemini image {(int)response.StatusCode}: {json}", null, response.StatusCode);
            }
            var root = JsonNode.Parse(json);
            var image = (root?["candidates"]?[0]?["content"]?["parts"] as JsonArray ?? [])
                .Select(p => p?["inlineData"] ?? p?["inline_data"])
                .FirstOrDefault(d => d?["data"] is not null);
            if (image is null)
            {
                var reason = root?["candidates"]?[0]?["finishReason"]?.ToString() ?? root?["promptFeedback"]?["blockReason"]?.ToString() ?? "unknown";
                throw new InvalidOperationException($"Gemini returned no image ({reason}).");
            }
            var mime = (image["mimeType"] ?? image["mime_type"])?.GetValue<string>() ?? "image/png";
            contents.Add(new DataContent(Convert.FromBase64String(image["data"]!.GetValue<string>()), mime));
        }
        return new ImageGenerationResponse(contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
