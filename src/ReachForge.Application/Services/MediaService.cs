using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>画像生成ジョブの要求（POST /ai/images）。</summary>
public sealed record ImageJobRequest
{
    public required string Prompt { get; init; }
    public ImageStyle Style { get; init; } = ImageStyle.Photo;

    /// <summary>生成する比率の基準にする SNS（既定：Instagram 4:5）。</summary>
    public SocialPlatform AspectFor { get; init; } = SocialPlatform.Instagram;
    public int Count { get; init; } = 1;
    public bool OverlayLogo { get; init; }
}

/// <summary>アウトペインティング（AI で広げる）ジョブの要求。完了するとバリアントの画像を差し替える。</summary>
public sealed record OutpaintJobRequest(Guid SourceAssetId, SocialPlatform Platform, Guid? VariantId);

public enum MediaFilter { All, Uploaded, AiGenerated }

/// <summary>メディアライブラリ（SCR-13）・取り込み・SNS 別の比率変換（F-04-6）・画像生成ジョブ（F-04）。</summary>
public sealed class MediaService(
    IAppDbContext db,
    ITenantContext tenant,
    IMediaStorage storage,
    IImageProcessor images,
    IMediaUrlSigner signer,
    IAltTextGenerator alt,
    ICreditService credits,
    IWorkQueue queue,
    TimeProvider clock)
{
    public const int MaxDimension = 4096;
    public const long InstagramMaxBytes = 8 * 1024 * 1024;
    public const long LineMaxBytes = 10 * 1024 * 1024;
    public static readonly TimeSpan UiUrlLifetime = TimeSpan.FromHours(1);
    public const string ThumbnailKey = "thumb:480";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>元画像の一覧（派生画像は含めない）。</summary>
    public async Task<IReadOnlyList<MediaAsset>> ListAsync(MediaFilter filter, CancellationToken ct)
    {
        var q = db.MediaAssets.Where(m => m.WorkspaceId == tenant.WorkspaceId && m.DerivationKey == null);
        q = filter switch
        {
            MediaFilter.Uploaded => q.Where(m => m.Source == MediaSource.Upload),
            MediaFilter.AiGenerated => q.Where(m => m.Source == MediaSource.AiGenerated || m.Source == MediaSource.AiEdited),
            _ => q,
        };
        return (await q.ToListAsync(ct)).OrderByDescending(m => m.CreatedAt).ToList();
    }

    public async Task<MediaAsset> GetAsync(Guid id, CancellationToken ct) =>
        await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("画像");

    public Task<string> UrlAsync(Guid id, CancellationToken ct) => signer.CreateReadUrlAsync(id, UiUrlLifetime, ct);

    /// <summary>画像を取り込む（JPEG / PNG / WebP、20MB まで）。向き補正・位置情報などのメタデータ削除・sRGB 化して保存する。</summary>
    public async Task<MediaAsset> UploadAsync(string fileName, string contentType, Stream content, long length, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (!MediaAsset.AcceptedUploadTypes.Contains(contentType))
        {
            throw new DomainException(ErrorCodes.Validation, "このファイル形式には対応していません。JPEG・PNG・WebP の画像を選んでください。");
        }
        if (length > MediaAsset.MaxUploadBytes)
        {
            throw new DomainException(ErrorCodes.Validation, "画像は20MBまでです。サイズを小さくしてから選んでください。");
        }

        ProcessedImage normalized;
        try
        {
            normalized = await images.NormalizeAsync(content, MaxDimension, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainException)
        {
            throw new DomainException(ErrorCodes.Validation, "画像を読み込めませんでした。別の画像を選んでください。");
        }

        var asset = await SaveAsync(normalized, MediaSource.Upload, Path.GetFileName(fileName), null, null, ct);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>
    /// 画像に文字を入れる（F-04 文字入れ）。日本語の文字は生成 AI に描かせず、フォントで正確に描く（0 クレジット）。
    /// 元の画像は残し、新しい画像としてライブラリに追加する。
    /// </summary>
    public async Task<MediaAsset> AddTextAsync(Guid id, TextOverlay overlay, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var headline = overlay.Headline.Trim();
        var sub = string.IsNullOrWhiteSpace(overlay.Sub) ? null : overlay.Sub.Trim();
        if (headline.Length == 0) throw new DomainException(ErrorCodes.Validation, "入れる文字を入力してください。");
        if (PostText.Length(headline) > TextOverlay.MaxHeadline || (sub is not null && PostText.Length(sub) > TextOverlay.MaxSub))
        {
            throw new DomainException(ErrorCodes.Validation, $"見出しは{TextOverlay.MaxHeadline}字、補足は{TextOverlay.MaxSub}字までです。");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(overlay.BandColorHex, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new DomainException(ErrorCodes.Validation, "帯の色は #RRGGBB の形式で指定してください。");
        }
        var source = await GetAsync(id, ct);
        ProcessedImage rendered;
        try
        {
            rendered = await images.RenderTextAsync(await ReadAsync(source, ct), overlay with { Headline = headline, Sub = sub }, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new DomainException(ErrorCodes.SysUnexpected, ex.Message);
        }
        var name = Path.GetFileNameWithoutExtension(source.FileName);
        var asset = await SaveAsync(rendered, MediaSource.Derived, $"{name}-文字入れ.jpg", source.Id, null, ct);
        asset.IsAiLabeled = source.IsAiLabeled;
        asset.Provenance = source.Provenance;
        asset.SafetyResult = source.SafetyResult;
        asset.AltText = string.IsNullOrWhiteSpace(source.AltText) ? $"「{headline}」の文字が入った画像" : $"{source.AltText}。「{headline}」の文字入り";
        asset.AltTextIsAi = source.AltTextIsAi;
        db.Record(tenant, "media.text_added", nameof(MediaAsset), asset.Id);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>合成した動画を保存し、先頭のシーンからサムネイル（LINE のプレビュー等）を作る。</summary>
    internal async Task<MediaAsset> SaveVideoAsync(ComposedVideo video, string fileName, byte[] firstFrame, string srt, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var path = $"{tenant.TenantId:N}/{tenant.WorkspaceId:N}/{id:N}.mp4";
        await storage.SaveAsync(path, video.Mp4, "video/mp4", ct);
        var asset = new MediaAsset
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Kind = MediaKind.Video,
            Source = MediaSource.AiGenerated,
            BlobPath = path,
            Mime = "video/mp4",
            FileName = fileName,
            Width = video.Width,
            Height = video.Height,
            DurationMs = video.DurationMs,
            Bytes = video.Mp4.LongLength,
            SubtitlesSrt = srt,
            SafetyResult = "ok",
        };
        typeof(MediaAsset).GetProperty(nameof(MediaAsset.Id))!.SetValue(asset, id);
        db.MediaAssets.Add(asset);
        await ThumbnailCoreAsync(asset, firstFrame, ct);
        return asset;
    }

    /// <summary>画面表示用：URL と種類（動画ならサムネイルの URL も）。</summary>
    public async Task<(string Url, MediaKind Kind, string? PosterUrl)> DisplayAsync(Guid id, CancellationToken ct)
    {
        var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("画像");
        var url = await UrlAsync(id, ct);
        if (asset.Kind != MediaKind.Video) return (url, asset.Kind, null);
        var thumb = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.ParentAssetId == id && m.DerivationKey == ThumbnailKey, ct);
        return (url, asset.Kind, thumb is null ? null : await UrlAsync(thumb.Id, ct));
    }

    /// <summary>ALT テキストを AI で作る（分類と同様にクレジットは消費しない）。</summary>
    public async Task<MediaAsset> GenerateAltAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var asset = await GetAsync(id, ct);
        // 動画はサムネイル（先頭のシーン）を見て説明する
        var target = asset.Kind == MediaKind.Video
            ? await db.MediaAssets.FirstOrDefaultAsync(m => m.ParentAssetId == asset.Id && m.DerivationKey == ThumbnailKey, ct) ?? asset
            : asset;
        if (target.Kind == MediaKind.Video) throw new DomainException(ErrorCodes.Validation, "この動画の説明は手入力してください。");
        var bytes = await ReadAsync(target, ct);
        asset.AltText = await alt.DescribeAsync(bytes, target.Mime, asset.FileName, ct);
        asset.AltTextIsAi = true;
        await db.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<MediaAsset> UpdateAsync(Guid id, string? altText, IEnumerable<string> tags, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var asset = await GetAsync(id, ct);
        if (asset.AltText != altText) asset.AltTextIsAi = false;
        asset.AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
        asset.Tags = [.. tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()];
        await db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>この画像（または派生画像）を使っている投稿バリアント。</summary>
    public async Task<IReadOnlyList<PostVariant>> UsagesAsync(Guid id, CancellationToken ct)
    {
        var family = await db.MediaAssets.Where(m => m.Id == id || m.ParentAssetId == id).Select(m => m.Id).ToListAsync(ct);
        return (await db.PostVariants.Where(v => v.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
            .Where(v => v.MediaAssetIds.Any(family.Contains))
            .ToList();
    }

    /// <summary>削除する。公開前の投稿で使用中の場合は削除できない（誤って画像のない投稿にしないため）。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var asset = await GetAsync(id, ct);
        var inUse = (await UsagesAsync(id, ct))
            .Where(v => v.Status is not (VariantStatus.Published or VariantStatus.Canceled or VariantStatus.Failed))
            .ToList();
        if (inUse.Count > 0)
        {
            throw new DomainException(ErrorCodes.Validation, $"この画像は公開前の投稿 {inUse.Count}件で使われているため削除できません。先に投稿から外してください。");
        }
        var family = await db.MediaAssets.Where(m => m.Id == id || m.ParentAssetId == id).ToListAsync(ct);
        foreach (var m in family)
        {
            await storage.DeleteAsync(m.BlobPath, ct);
            db.MediaAssets.Remove(m);
        }
        db.Record(tenant, "media.deleted", nameof(MediaAsset), asset.Id, asset.FileName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// SNS の推奨比率・形式の派生画像を作る（または既存を再利用する）。比率差 2% 以下は縮小のみ。
    /// Instagram は JPEG（sRGB・8MB 以下）、その他も JPEG で 10MB 以下にする。
    /// </summary>
    public async Task<MediaAsset> DeriveForPlatformAsync(MediaAsset source, SocialPlatform platform, AspectMethod method,
        CancellationToken ct)
    {
        if (method == AspectMethod.Outpaint)
        {
            throw new DomainException(ErrorCodes.Validation, "「AIで広げる」は EnqueueOutpaintAsync で依頼してください。");
        }
        if (source.Kind == MediaKind.Video) return source; // 動画は 9:16 で作成済み（変換しない）
        var c = PlatformCatalog.Get(platform);
        var key = $"{platform}:{c.ImageSize.Width}x{c.ImageSize.Height}:{method}";
        var existing = await db.MediaAssets.FirstOrDefaultAsync(m => m.ParentAssetId == source.Id && m.DerivationKey == key, ct);
        if (existing is not null) return existing;

        var bytes = await ReadAsync(source, ct);
        var pad = await PadColorAsync(ct);
        var converted = await images.ConvertAspectAsync(bytes, c.ImageAspect, c.ImageSize, method, pad, ct);
        var encoded = await images.EncodeJpegAsync(converted.Bytes, platform == SocialPlatform.Instagram ? InstagramMaxBytes : LineMaxBytes, ct);

        var derived = await SaveAsync(encoded, MediaSource.Derived, source.FileName, source.Id, key, ct);
        derived.IsAiLabeled = source.IsAiLabeled;
        derived.AltText = source.AltText;
        derived.AltTextIsAi = source.AltTextIsAi;
        derived.SafetyResult = source.SafetyResult;
        derived.Provenance = source.Provenance;
        if (platform == SocialPlatform.Line) await ThumbnailCoreAsync(derived, encoded.Bytes, ct); // LINE のプレビュー画像（1MB 以下）
        return derived;
    }

    /// <summary>サムネイル（一覧表示・LINE のプレビュー画像：1MB 以下）。</summary>
    public async Task<MediaAsset> ThumbnailAsync(MediaAsset source, CancellationToken ct)
    {
        const string key = ThumbnailKey;
        var existing = await db.MediaAssets.FirstOrDefaultAsync(m => m.ParentAssetId == source.Id && m.DerivationKey == key, ct);
        if (existing is not null) return existing;
        var asset = await ThumbnailCoreAsync(source, await ReadAsync(source, ct), ct);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    private async Task<MediaAsset> ThumbnailCoreAsync(MediaAsset source, byte[] bytes, CancellationToken ct)
    {
        var thumb = await images.ThumbnailAsync(bytes, 480, ct);
        var asset = await SaveAsync(await images.EncodeJpegAsync(thumb.Bytes, 1_000_000, ct), MediaSource.Derived, source.FileName,
            source.Id, ThumbnailKey, ct);
        asset.IsAiLabeled = source.IsAiLabeled;
        return asset;
    }

    /// <summary>画像生成ジョブを登録する（推定 5 クレジット／枚を予約）。完了は GetJobAsync で確認する。</summary>
    public async Task<AiJob> EnqueueGenerationAsync(ImageJobRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new DomainException(ErrorCodes.Validation, "どんな画像にするかを入力してください。例：木のテーブルに置かれた湯気の立つラテ");
        }
        if (PostText.Length(request.Prompt) > ImageGenerationSpec.MaxPromptLength)
        {
            throw new DomainException(ErrorCodes.Validation, $"画像の説明は{ImageGenerationSpec.MaxPromptLength}字以内で入力してください。");
        }
        if (Domain.Guardrails.PromptInjectionDetector.IsSuspicious(request.Prompt))
        {
            throw new AiSafetyBlockedException("指示の書き換えの疑い");
        }
        var count = Math.Clamp(request.Count, 1, ImageGenerationSpec.MaxCount);
        return await EnqueueAsync(AiTaskType.Image, request with { Count = count },
            CreditTable.Cost(CreditOperation.ImageStandard, count), ct);
    }

    /// <summary>AI で画像を広げる（アウトペインティング）ジョブを登録する（5 クレジット）。</summary>
    public async Task<AiJob> EnqueueOutpaintAsync(OutpaintJobRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        await GetAsync(request.SourceAssetId, ct);
        return await EnqueueAsync(AiTaskType.ImageEdit, request, CreditTable.Cost(CreditOperation.ImageEditAi), ct);
    }

    /// <summary>ジョブの状態（Worker が更新するため追跡せずに毎回読む）。</summary>
    public async Task<AiJob> GetJobAsync(Guid id, CancellationToken ct) =>
        await db.AiJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw new NotFoundException("処理");

    /// <summary>待機中のジョブを取り消す（予約したクレジットを解放する）。</summary>
    public async Task CancelJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.AiJobs.FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw new NotFoundException("処理");
        await db.ReloadAsync(job, ct);
        job.Cancel(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await credits.ReleaseReservedAsync(job.CreditsHeld, ct);
    }

    internal async Task<byte[]> ReadAsync(MediaAsset asset, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(asset.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>保存して MediaAsset を追加する（SaveChanges は呼び出し側）。</summary>
    internal async Task<MediaAsset> SaveAsync(ProcessedImage image, MediaSource source, string fileName, Guid? parentId,
        string? derivationKey, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var ext = image.Mime switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };
        var path = $"{tenant.TenantId:N}/{tenant.WorkspaceId:N}/{id:N}.{ext}";
        await storage.SaveAsync(path, image.Bytes, image.Mime, ct);

        var asset = new MediaAsset
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Kind = MediaKind.Image,
            Source = source,
            BlobPath = path,
            Mime = image.Mime,
            FileName = string.IsNullOrWhiteSpace(fileName) ? $"{id:N}.{ext}" : fileName,
            Width = image.Width,
            Height = image.Height,
            Bytes = image.Bytes.LongLength,
            ParentAssetId = parentId,
            DerivationKey = derivationKey,
        };
        typeof(MediaAsset).GetProperty(nameof(MediaAsset.Id))!.SetValue(asset, id);
        db.MediaAssets.Add(asset);
        return asset;
    }

    internal async Task<string> PadColorAsync(CancellationToken ct)
    {
        var brand = await db.BrandProfiles.FirstOrDefaultAsync(b => b.WorkspaceId == tenant.WorkspaceId, ct);
        return brand?.BrandColors.FirstOrDefault() ?? "#FFFFFF";
    }

    private async Task<AiJob> EnqueueAsync<T>(AiTaskType type, T request, int amount, CancellationToken ct)
    {
        await credits.ReserveAsync(amount, ct);
        try
        {
            var job = new AiJob
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                TaskType = type,
                RequestJson = JsonSerializer.Serialize(request, s_json),
                CreditsHeld = amount,
                RequestedBy = tenant.UserName,
            };
            db.AiJobs.Add(job);
            db.Record(tenant, "ai.job_queued", nameof(AiJob), job.Id, type.ToString());
            await db.SaveChangesAsync(ct);
            await queue.NotifyAiJobAsync(job.Id, ct);
            return job;
        }
        catch
        {
            await credits.ReleaseReservedAsync(amount, CancellationToken.None);
            throw;
        }
    }

    internal static T Request<T>(AiJob job) => JsonSerializer.Deserialize<T>(job.RequestJson, s_json)!;
}
