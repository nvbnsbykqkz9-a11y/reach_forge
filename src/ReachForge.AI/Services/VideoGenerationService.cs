using System.Diagnostics;
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
/// 生成 AI の動画（F-05 ①②）。VideoGeneration のルートに従ってプロバイダを選び、失敗時は代替プロバイダへ。
/// 全体で 15 分を超えたら失敗とし、テンプレート合成への切り替えを案内する（F-05 例外）。原価は秒数×単価で記録する。
/// </summary>
public sealed class VideoGenerationService(
    IVideoGeneratorFactory factory,
    ProviderCircuitBreaker breaker,
    IAiUsageSink usage,
    ITenantContext tenant,
    IOptions<AiOptions> options,
    ILogger<VideoGenerationService> log) : IVideoGenerationService
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

    public async Task<GeneratedVideo> GenerateAsync(VideoGenerationSpec spec, Guid? generationId, CancellationToken ct)
    {
        var o = options.Value;
        var route = spec.Provider is { Length: > 0 } forced ? [forced] : o.RouteFor(AiTaskType.VideoGeneration);
        var candidates = route
            .Where(n => o.Providers.TryGetValue(n, out var p) && p.IsConfigured && p.SupportsVideoGeneration)
            .Select(n => (Name: n, Options: o.Providers[n]))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new AiUnavailableException(spec.Provider is { Length: > 0 }
                ? $"「{spec.Provider}」は動画生成に使えません（API キーが未設定か、動画に対応していません）。"
                : "動画生成のAIが設定されていません。テンプレート合成をお使いください。");
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(Timeout);
        Exception? last = null;
        var attempted = 0;
        foreach (var (name, provider) in candidates)
        {
            var key = ProviderCircuitBreaker.Key(name, ProviderCircuitBreaker.Video);
            if (breaker.IsOpen(key) && spec.Provider is null) continue;
            var fallback = attempted++ > 0;
            var modelId = spec.Model is { Length: > 0 } model ? model.Trim() : DefaultModel(provider);
            var sw = Stopwatch.StartNew();
            try
            {
                var mp4 = await factory.Get(name, provider).GenerateAsync(spec, modelId, overall.Token);
                if (mp4.Length == 0) throw new InvalidOperationException("Empty video");
                breaker.RecordSuccess(key);
                Record(name, provider, modelId, spec.Seconds, sw, fallback, true, generationId);
                return new GeneratedVideo(mp4, new AiModelInfo(name, modelId, fallback));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && overall.IsCancellationRequested)
            {
                Record(name, provider, modelId, 0, sw, fallback, false, generationId);
                throw new AiUnavailableException(
                    "動画の生成が15分以内に終わりませんでした。画像とテロップで作る「テンプレート合成」をお試しください。");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                breaker.RecordFailure(key);
                Record(name, provider, modelId, 0, sw, fallback, false, generationId);
                log.LogWarning(ex, "Video provider {Provider} failed; trying next", name);
            }
        }
        throw new AiUnavailableException(spec.Provider is { Length: > 0 } && last is not null
            ? $"動画を生成できませんでした：{last.Message}"
            : "動画を生成できませんでした。時間をおいて試すか、テンプレート合成をお使いください。", last);
    }

    /// <summary>設定のモデル（なければ各社の既定のモデル）。</summary>
    private static string DefaultModel(AiProviderOptions provider) =>
        provider.ModelFor(AiTaskType.VideoGeneration) ?? provider.Type switch
        {
            AiProviderType.Kling => KlingVideoGenerator.DefaultModel,
            AiProviderType.Google => VeoVideoGenerator.DefaultModel,
            AiProviderType.OpenAI => "sora-2",
            _ => "",
        };

    public IReadOnlyList<VideoProviderInfo> Providers()
    {
        var o = options.Value;
        var route = o.RouteFor(AiTaskType.VideoGeneration);
        // ルートの順に、ほかに設定されている動画対応のプロバイダも続ける（ローカル用のスタブはルートにあるときだけ）
        return [.. route.Concat(o.Providers.Keys.Order(StringComparer.Ordinal)).Distinct()
            .Where(n => o.Providers.TryGetValue(n, out var p) && p.SupportsVideoGeneration && (p.Type != AiProviderType.Stub || route.Contains(n)))
            .Select(n =>
            {
                var p = o.Providers[n];
                return new VideoProviderInfo(n, p.Type.ToString(), p.IsConfigured, DefaultModel(p), p.PricePerVideoSecond);
            })];
    }

    private void Record(string name, AiProviderOptions provider, string modelId, int seconds, Stopwatch sw, bool fallback, bool succeeded,
        Guid? generationId) =>
        usage.Add(new AiUsageLog
        {
            TenantId = tenant.TenantId,
            AiGenerationId = generationId,
            TaskType = AiTaskType.VideoGeneration,
            Provider = name,
            ModelId = modelId,
            VideoSeconds = seconds,
            CostUsd = seconds * provider.PricePerVideoSecond,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            FallbackUsed = fallback,
            Succeeded = succeeded,
        });
}
