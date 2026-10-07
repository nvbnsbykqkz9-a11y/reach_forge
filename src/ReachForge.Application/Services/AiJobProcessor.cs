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
                AiTaskType.ImageEdit => MediaService.EditKindOf(job) is null ? await OutpaintAsync(job, ct) : await ReferenceEditAsync(job, ct),
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

            var asset = await media.SaveAsync(normalized, MediaSource.AiGenerated, $"ai-{saved.Count + 1}.jpg", null, null, ct,
                new ProvenanceInfo(DigitalSourceType.TrainedAlgorithmicMedia, "c2pa.created", image.Model.Provider, image.Model.ModelId,
                    clock.GetUtcNow(), $"ai-{saved.Count + 1}.jpg"));
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
            $"{request.Platform}:{c.ImageSize.Width}x{c.ImageSize.Height}:{AspectMethod.Outpaint}:{job.Id:N}", ct,
            new ProvenanceInfo(DigitalSourceType.CompositeWithTrainedAlgorithmicMedia, "c2pa.edited", image.Model.Provider, image.Model.ModelId,
                clock.GetUtcNow(), source.FileName));
        asset.IsAiLabeled = true;
        asset.SafetyResult = verdict.Result;
        asset.AiGenerationId = job.Id;
        asset.Provenance = Provenance(image.Model, "outpaint", source.Id.ToString());
        asset.AltText = source.AltText;

        return [asset];
    }

    /// <summary>
    /// 参照画像の編集（F-04）。AI に描かせるのは背景・消した部分だけで、それ以外は元の画素を戻す。
    /// 商品の配置では商品を切り抜いて重ねるため、商品は生成・改変しない。
    /// </summary>
    private async Task<List<MediaAsset>> ReferenceEditAsync(AiJob job, CancellationToken ct)
    {
        var request = MediaService.Request<ReferenceEditJobRequest>(job);
        var source = await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == request.SourceAssetId, ct) ?? throw new NotFoundException("元の画像");
        var original = await media.ReadAsync(source, ct);
        var aspect = new AspectRatio(source.Width ?? 1, source.Height ?? 1);
        var size = (source.Width ?? 1024, source.Height ?? 1024);
        byte[] result;
        GeneratedImage generated;
        var subjectPreserved = true;

        switch (request.Kind)
        {
            case ImageEditKind.ObjectRemoval:
            {
                var canvas = await images.PrepareEraseCanvasAsync(original, request.Regions, ct);
                generated = await GenerateOneAsync(new ImageGenerationSpec
                {
                    Prompt = "透明な部分を、周りの背景に自然につながるように描き直してください。そこにあった物は描かないでください。"
                             + (request.Prompt.Length > 0 ? $"補足：{request.Prompt}" : ""),
                    Aspect = aspect, Size = size, SourceImage = canvas.Bytes, SourceMime = canvas.Mime,
                }, job, ct);
                result = (await images.RestoreAsync(original, generated.Bytes, request.Regions, null, ct)).Bytes;
                break;
            }
            case ImageEditKind.BackgroundReplace:
            {
                var cutout = await images.CutoutAsync(original, ct);
                generated = await GenerateOneAsync(new ImageGenerationSpec
                {
                    Prompt = $"背景を「{request.Prompt}」に差し替えてください。被写体（商品・人物）の形・色・文字は変えないでください。",
                    Aspect = aspect, Size = size, SourceImage = original, SourceMime = source.Mime,
                }, job, ct);
                subjectPreserved = cutout.Confidence >= Cutout.MinConfidence;
                result = subjectPreserved
                    ? (await images.RestoreAsync(original, generated.Bytes, null, cutout.Image.Bytes, ct)).Bytes
                    : generated.Bytes; // 背景が複雑で切り抜けない写真は AI の結果をそのまま使う（来歴に記録）
                break;
            }
            case ImageEditKind.ProductPlacement:
            {
                var cutout = await images.CutoutAsync(original, ct);
                if (cutout.Confidence < Cutout.MinConfidence)
                {
                    throw new DomainException(ErrorCodes.Validation,
                        "商品を背景から切り抜けませんでした。白や単色の背景で撮った商品写真、または背景を透明にした PNG を選んでください（クレジットは消費されていません）。");
                }
                var c = PlatformCatalog.Get(request.AspectFor);
                generated = await GenerateOneAsync(new ImageGenerationSpec
                {
                    Prompt = $"{request.Prompt}。商品を置くための背景で、{(request.Placement == ProductPlacement.Center ? "中央" : "手前")}に何も置かれていない空間を残す。",
                    Style = ImageStyle.Photo, Aspect = c.ImageAspect, Size = c.ImageSize,
                }, job, ct);
                result = (await images.CompositeAsync(generated.Bytes, cutout.Image.Bytes, request.Placement, request.Scale, ct)).Bytes;
                break;
            }
            default:
                throw new DomainException(ErrorCodes.Validation, "未対応の編集です。");
        }
        job.MoveTo(AiJobStage.Checking);

        var verdict = await safety.CheckAsync(result, "image/png", ct);
        if (verdict.Blocked) return [];
        using var input = new MemoryStream(result);
        var normalized = await images.NormalizeAsync(input, MediaService.MaxDimension, ct);
        // 編集結果は利用者が選んで使う新しい画像（ライブラリに表示する）。SNS 用の派生画像のような DerivationKey は付けない
        var name = Path.GetFileNameWithoutExtension(source.FileName);
        var label = request.Kind switch
        {
            ImageEditKind.BackgroundReplace => "背景差替",
            ImageEditKind.ObjectRemoval => "不要物除去",
            _ => "商品配置",
        };
        var asset = await media.SaveAsync(normalized, MediaSource.AiEdited, $"{name}-{label}.jpg", source.Id, null, ct,
            new ProvenanceInfo(DigitalSourceType.CompositeWithTrainedAlgorithmicMedia, "c2pa.edited", generated.Model.Provider,
                generated.Model.ModelId, clock.GetUtcNow(), source.FileName));
        asset.IsAiLabeled = true;
        asset.SafetyResult = verdict.Result;
        asset.AiGenerationId = job.Id;
        asset.Provenance = Provenance(generated.Model, request.Prompt,
            JsonSerializer.Serialize(new { edit = request.Kind.ToString(), source = source.Id, subjectPreserved }));
        asset.AltText = request.Kind == ImageEditKind.ObjectRemoval ? source.AltText : await TryAltAsync(normalized, request.Prompt, ct);
        asset.AltTextIsAi = asset.AltText is not null && request.Kind != ImageEditKind.ObjectRemoval;
        return [asset];
    }

    private async Task<GeneratedImage> GenerateOneAsync(ImageGenerationSpec spec, AiJob job, CancellationToken ct) =>
        (await generator.GenerateAsync(spec, job.Id, ct)).FirstOrDefault() ?? throw new AiUnavailableException("AIから画像を受け取れませんでした。");

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
