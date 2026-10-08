using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Services;

/// <summary>
/// 画像の生成・編集（RF-DES-001 4.2 / F-04）。タスク（Image / ImageEdit）のルートに従いプロバイダを選び、
/// 失敗・タイムアウト（120秒）時は代替プロバイダで再試行する。生成枚数と推定原価を ai_usage_log に記録する。
/// </summary>
public sealed class ImageGenerationService(
    IImageGeneratorFactory factory,
    ProviderCircuitBreaker breaker,
    IAiUsageSink usage,
    ITenantContext tenant,
    IOptions<AiOptions> options,
    ILogger<ImageGenerationService> log) : IImageGenerationService
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(120);

    public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationSpec spec, Guid? generationId, CancellationToken ct)
    {
        var task = spec.SourceImage is null ? AiTaskType.Image : AiTaskType.ImageEdit;
        var o = options.Value;
        var candidates = o.RouteFor(task)
            .Where(n => o.Providers.TryGetValue(n, out var p) && p.IsConfigured && p.SupportsImageGeneration)
            .Select(n => (Name: n, Options: o.Providers[n]))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new AiUnavailableException("画像生成のAIが設定されていません。運用管理画面でAIモデル設定を確認してください。");
        }

        Exception? last = null;
        var attempted = 0;
        foreach (var (name, provider) in candidates)
        {
            var key = ProviderCircuitBreaker.Key(name, ProviderCircuitBreaker.Image);
            if (breaker.IsOpen(key)) continue;
            var fallback = attempted++ > 0;
            var modelId = provider.ModelFor(task) ?? "";
            var sw = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                var generator = factory.Get(name, provider, modelId);
                var request = new ImageGenerationRequest(BuildPrompt(spec));
                if (spec.SourceImage is not null)
                {
                    request.OriginalImages = [new DataContent(spec.SourceImage, spec.SourceMime ?? "image/png")];
                }
                var response = await generator.GenerateAsync(request, BuildOptions(spec, provider, modelId), timeout.Token);
                var images = response.Contents.OfType<DataContent>()
                    .Where(d => d.MediaType.StartsWith("image/", StringComparison.Ordinal))
                    .Select(d => new GeneratedImage(d.Data.ToArray(), d.MediaType, new AiModelInfo(name, modelId, fallback)))
                    .ToList();
                if (images.Count == 0) throw new InvalidOperationException("No image content returned.");

                breaker.RecordSuccess(key);
                Record(task, name, provider, modelId, images.Count, sw, fallback, true, generationId);
                return images;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                breaker.RecordFailure(key);
                Record(task, name, provider, modelId, 0, sw, fallback, false, generationId);
                log.LogWarning(ex, "Image provider {Provider} failed for {Task}; trying next", name, task);
            }
        }
        throw new AiUnavailableException("画像の作成に失敗しました。AIが混み合っている可能性があります。もう一度お試しください。", last);
    }

    /// <summary>画像プロンプトの設計（Creative Agent 相当）。写真調などのスタイルとブランドカラーを指定し、文字・実在人物・商標は避ける。</summary>
    internal static string BuildPrompt(ImageGenerationSpec spec)
    {
        if (spec.SourceImage is not null) return spec.Prompt;
        var style = spec.Style switch
        {
            ImageStyle.Photo => "High-quality natural-light photograph, realistic, shallow depth of field",
            ImageStyle.Illustration => "Warm hand-drawn illustration with soft textures",
            ImageStyle.Flat => "Clean flat vector illustration with simple shapes",
            ImageStyle.ThreeD => "Soft 3D render with gentle lighting",
            _ => "High-quality image",
        };
        var colors = spec.BrandColors.Count > 0 ? $" Use a color palette based on {string.Join(", ", spec.BrandColors)}." : "";
        return $"{style} for a social media post{(string.IsNullOrWhiteSpace(spec.BrandName) ? "" : $" by \"{spec.BrandName}\"")}. " +
               $"Subject (in Japanese): {spec.Prompt}.{colors} Composition for aspect ratio {spec.Aspect}. " +
               "Do not render any text, logos, watermarks, real people, celebrities, or trademarked characters.";
    }

    private static ImageGenerationOptions BuildOptions(ImageGenerationSpec spec, AiProviderOptions provider, string modelId)
    {
        var options = new ImageGenerationOptions
        {
            Count = spec.SourceImage is null ? spec.Count : 1,
            ModelId = string.IsNullOrEmpty(modelId) ? null : modelId,
            ResponseFormat = ImageGenerationResponseFormat.Data,
            MediaType = "image/png",
            ImageSize = provider.Type == AiProviderType.OpenAI
                ? NearestOpenAiSize(spec.Aspect.Value)
                : new System.Drawing.Size(spec.Size.Width, spec.Size.Height),
            AdditionalProperties = new AdditionalPropertiesDictionary { [StubImageGenerator.ColorsKey] = spec.BrandColors },
        };
        return options;
    }

    /// <summary>gpt-image の対応サイズ（正方形・縦長・横長）のうち近いもの。最終的な比率は後段のスマートクロップで合わせる。</summary>
    internal static System.Drawing.Size NearestOpenAiSize(double ratio) => ratio switch
    {
        > 1.15 => new(1536, 1024),
        < 0.87 => new(1024, 1536),
        _ => new(1024, 1024),
    };

    private void Record(AiTaskType task, string name, AiProviderOptions provider, string modelId, int images, Stopwatch sw,
        bool fallback, bool succeeded, Guid? generationId) =>
        usage.Add(new AiUsageLog
        {
            TenantId = tenant.TenantId,
            AiGenerationId = generationId,
            TaskType = task,
            Provider = name,
            ModelId = modelId,
            Images = images,
            CostUsd = images * provider.PricePerImage,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            FallbackUsed = fallback,
            Succeeded = succeeded,
        });
}
