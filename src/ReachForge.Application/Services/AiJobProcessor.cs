using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>
/// AI ジョブ（画像生成・アウトペインティング）を1件実行する（RF-DES-001 F-04 処理 1〜7）。
/// ジョブのテナント・ワークスペースのコンテキストで呼び出すこと（Worker の AiJobDispatcher が設定する）。
/// 成功した画像の枚数分だけクレジットを確定し、失敗・安全性ブロック時は解放する（クレジットを消費しない）。
/// </summary>
public sealed class AiJobProcessor(
    IAppDbContext db,
    MediaService media,
    IImageGenerationService generator,
    IImageProcessor images,
    IImageSafetyChecker safety,
    IAltTextGenerator alt,
    ICreditService credits,
    ReportService reports,
    VideoService videos,
    TimeProvider clock,
    ILogger<AiJobProcessor> log)
{
    /// <summary>実行中のまま止まったジョブを失敗扱いにする時間（F-04 例外：生成タイムアウト 120 秒＋再試行の余裕）。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.AiJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != AiJobStatus.Queued) return; // 他の Worker が処理済み

        job.Start(clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct); // 楽観排他で二重実行を防ぐ
        }
        catch (DbUpdateConcurrencyException)
        {
            return;
        }

        if (job.TaskType == AiTaskType.Report)
        {
            await ProcessReportAsync(job, ct);
            return;
        }
        if (job.TaskType == AiTaskType.Video)
        {
            await ProcessVideoAsync(job, ct);
            return;
        }

        try
        {
            var assets = job.TaskType switch
            {
                AiTaskType.Image => await GenerateAsync(job, ct),
                AiTaskType.ImageEdit => await OutpaintAsync(job, ct),
                _ => throw new DomainException(ErrorCodes.Validation, $"未対応のジョブ種別です（{job.TaskType}）"),
            };
            if (assets.Count == 0)
            {
                throw new AiSafetyBlockedException("画像の安全性");
            }

            var perImage = job.TaskType == AiTaskType.ImageEdit
                ? CreditTable.Cost(CreditOperation.ImageEditAi)
                : CreditTable.Cost(CreditOperation.ImageStandard);
            var charged = await credits.CommitReservedAsync(job.CreditsHeld, perImage * assets.Count, ct);
            job.Succeed(assets.Select(a => a.Id), charged, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = ex is DomainException d ? d.ErrorCode : ErrorCodes.SysUnexpected;
            var message = ex is DomainException ? ex.Message : "画像を作成できませんでした。もう一度お試しください（クレジットは消費されていません）。";
            if (ex is not DomainException) log.LogError(ex, "AI job {JobId} failed", job.Id);
            DiscardUnsaved();
            job.Fail(code, message, clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            await credits.ReleaseReservedAsync(job.CreditsHeld, CancellationToken.None);
        }
    }

    /// <summary>ショート動画（テンプレート合成）。成功時に実際の長さで確定し、失敗時は予約を解放する。</summary>
    private async Task ProcessVideoAsync(AiJob job, CancellationToken ct)
    {
        try
        {
            var (video, actual) = await videos.ProcessAsync(job, ct);
            var charged = await credits.CommitReservedAsync(job.CreditsHeld, actual, ct);
            job.Succeed([video.Id], charged, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is not DomainException) log.LogError(ex, "Video job {JobId} failed", job.Id);
            DiscardUnsaved();
            job.Fail(ex is DomainException d ? d.ErrorCode : ErrorCodes.SysUnexpected,
                ex is DomainException ? ex.Message : "動画を作成できませんでした。もう一度お試しください（クレジットは消費されていません）。", clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            await credits.ReleaseReservedAsync(job.CreditsHeld, CancellationToken.None);
        }
    }

    /// <summary>AI レポート（クレジットは消費しない）。失敗の内容はレポート側にも記録される。</summary>
    private async Task ProcessReportAsync(AiJob job, CancellationToken ct)
    {
        try
        {
            await reports.ProcessAsync(job, ct);
            job.Succeed([], 0, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is not DomainException) log.LogError(ex, "Report job {JobId} failed", job.Id);
            job.Fail(ex is DomainException d ? d.ErrorCode : ErrorCodes.SysUnexpected,
                ex is DomainException ? ex.Message : "レポートを作成できませんでした。もう一度お試しください。", clock.GetUtcNow());
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>止まったジョブを失敗にしてクレジットを解放する。</summary>
    public async Task<int> FailStaleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stale = (await db.AiJobs.Where(j => j.Status == AiJobStatus.Running).ToListAsync(ct))
            .Where(j => j.IsStale(now, StaleAfter))
            .ToList();
        foreach (var job in stale)
        {
            job.Fail(ErrorCodes.AiUnavailable, "処理が時間内に終わりませんでした。もう一度お試しください（クレジットは消費されていません）。", now);
            await db.SaveChangesAsync(ct);
            await credits.ReleaseReservedAsync(job.CreditsHeld, ct);
        }
        return stale.Count;
    }

    private async Task<List<MediaAsset>> GenerateAsync(AiJob job, CancellationToken ct)
    {
        var request = MediaService.Request<ImageJobRequest>(job);
        var brand = await db.BrandProfiles.FirstOrDefaultAsync(b => b.WorkspaceId == job.WorkspaceId, ct);
        var c = PlatformCatalog.Get(request.AspectFor);
        var spec = new ImageGenerationSpec
        {
            Prompt = request.Prompt,
            Style = request.Style,
            Aspect = c.ImageAspect,
            Size = c.ImageSize,
            Count = request.Count,
            BrandColors = brand?.BrandColors ?? [],
            BrandName = brand?.BrandName ?? "",
        };

        var generated = await generator.GenerateAsync(spec, job.Id, ct);
        job.MoveTo(AiJobStage.Checking);
        await db.SaveChangesAsync(ct);

        byte[]? logo = null;
        if (request.OverlayLogo && brand?.LogoAssetId is { } logoId &&
            await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == logoId, ct) is { } logoAsset)
        {
            logo = await media.ReadAsync(logoAsset, ct);
        }

        var saved = new List<MediaAsset>();
        foreach (var image in generated)
        {
            var verdict = await safety.CheckAsync(image.Bytes, image.Mime, ct);
            if (verdict.Blocked)
            {
                log.LogInformation("Generated image blocked by safety check ({Result}) for job {JobId}", verdict.Result, job.Id);
                continue; // 該当画像は破棄（クレジットは消費しない）
            }

            using var input = new MemoryStream(image.Bytes);
            var normalized = await images.NormalizeAsync(input, MediaService.MaxDimension, ct);
            if (logo is not null) normalized = await images.OverlayLogoAsync(normalized.Bytes, logo, 0.18, ct);

            var asset = await media.SaveAsync(normalized, MediaSource.AiGenerated, $"ai-{saved.Count + 1}.jpg", null, null, ct);
            asset.IsAiLabeled = true;
            asset.SafetyResult = verdict.Result;
            asset.AiGenerationId = job.Id;
            asset.Provenance = Provenance(image.Model, request.Prompt, request.Style.ToString());
            asset.AltText = await TryAltAsync(normalized, request.Prompt, ct);
            asset.AltTextIsAi = asset.AltText is not null;
            saved.Add(asset);
        }
        return saved;
    }

    private async Task<List<MediaAsset>> OutpaintAsync(AiJob job, CancellationToken ct)
    {
        var request = MediaService.Request<OutpaintJobRequest>(job);
        var source = await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == request.SourceAssetId, ct)
                     ?? throw new NotFoundException("元の画像");
        var c = PlatformCatalog.Get(request.Platform);
        var canvas = await images.PrepareOutpaintCanvasAsync(await media.ReadAsync(source, ct), c.ImageAspect, c.ImageSize, ct);

        var generated = await generator.GenerateAsync(new ImageGenerationSpec
        {
            Prompt = "透明な部分を、元の画像に自然につながるように描き足してください。被写体や文字は変えないでください。",
            Aspect = c.ImageAspect,
            Size = c.ImageSize,
            SourceImage = canvas.Bytes,
            SourceMime = canvas.Mime,
        }, job.Id, ct);
        job.MoveTo(AiJobStage.Checking);

        var image = generated.FirstOrDefault() ?? throw new AiUnavailableException("AIから画像を受け取れませんでした。");
        var verdict = await safety.CheckAsync(image.Bytes, image.Mime, ct);
        if (verdict.Blocked) return [];

        // 生成結果の比率がずれていても、最後に目標比率へ合わせる（引き伸ばしはしない）
        var fitted = await images.ConvertAspectAsync(image.Bytes, c.ImageAspect, c.ImageSize, AspectMethod.SmartCrop, "#FFFFFF", ct);
        var encoded = await images.EncodeJpegAsync(fitted.Bytes,
            request.Platform == SocialPlatform.Instagram ? MediaService.InstagramMaxBytes : MediaService.LineMaxBytes, ct);
        var asset = await media.SaveAsync(encoded, MediaSource.AiEdited, source.FileName, source.Id,
            $"{request.Platform}:{c.ImageSize.Width}x{c.ImageSize.Height}:{AspectMethod.Outpaint}:{job.Id:N}", ct);
        asset.IsAiLabeled = true;
        asset.SafetyResult = verdict.Result;
        asset.AiGenerationId = job.Id;
        asset.Provenance = Provenance(image.Model, "outpaint", source.Id.ToString());
        asset.AltText = source.AltText;

        if (request.VariantId is { } variantId && await db.PostVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct) is { } variant)
        {
            var workspace = await db.Workspaces.FirstAsync(w => w.Id == variant.WorkspaceId, ct);
            variant.AspectMethod = AspectMethod.Outpaint;
            variant.SetMedia([asset.Id, .. variant.MediaAssetIds.Skip(1)], workspace.RequiresApproval);
        }
        return [asset];
    }

    private async Task<string?> TryAltAsync(ProcessedImage image, string hint, CancellationToken ct)
    {
        try
        {
            return await alt.DescribeAsync(image.Bytes, image.Mime, hint, ct);
        }
        catch (DomainException ex)
        {
            log.LogWarning(ex, "ALT text generation failed");
            return null; // ALT はメディアライブラリで後から作成・編集できる
        }
    }

    private void DiscardUnsaved()
    {
        if (db is DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries<MediaAsset>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private string Provenance(AiModelInfo model, string prompt, string detail) => JsonSerializer.Serialize(new
    {
        generator = "ReachForge",
        provider = model.Provider,
        model = model.ModelId,
        fallbackUsed = model.FallbackUsed,
        promptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
        detail,
        createdAt = clock.GetUtcNow(),
        aiGenerated = true,
    });
}
