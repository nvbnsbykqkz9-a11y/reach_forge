using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record PublishRunResult(int Published, int Retrying, int Failed, int Held);

/// <summary>
/// 予約投稿の実行（F-08-3〜5 / 14章 PublishJob）。Worker から定期的に呼び出す。
/// テナント横断で動作するため <see cref="ITenantContext.IsSystem"/> のコンテキストで実行すること。
/// 二重投稿防止：Scheduled → Publishing への遷移を楽観排他（row_version）で確定できたものだけを処理する。
/// </summary>
public sealed class PublishingService(
    IAppDbContext db,
    ITenantContext context,
    IPublisherFactory publishers,
    ChannelTokenService tokens,
    IMediaStorage storage,
    IMediaUrlSigner signer,
    TimeProvider clock,
    ILogger<PublishingService> log)
{
    public const int BatchSize = 50;

    public async Task<PublishRunResult> RunDueAsync(CancellationToken ct)
    {
        if (!context.IsSystem)
        {
            throw new InvalidOperationException("PublishingService はシステムコンテキストで実行してください。");
        }

        var now = clock.GetUtcNow();
        var held = await HoldExpiredApprovalsAsync(now, ct);

        var pausedTenants = await db.Tenants.Where(t => t.PublishingPaused).Select(t => t.Id).ToListAsync(ct);
        var dueIds = await db.PostVariants
            .Where(v => v.Status == VariantStatus.Scheduled && v.NextAttemptAt <= now && !pausedTenants.Contains(v.TenantId))
            .OrderBy(v => v.NextAttemptAt)
            .Select(v => v.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        int published = 0, retrying = 0, failed = 0;
        foreach (var id in dueIds)
        {
            var outcome = await PublishOneAsync(id, ct);
            switch (outcome)
            {
                case VariantStatus.Published: published++; break;
                case VariantStatus.Scheduled: retrying++; break;
                case VariantStatus.Failed: failed++; break;
            }
        }
        return new PublishRunResult(published, retrying, failed, held);
    }

    /// <summary>
    /// 今すぐ投稿する（つくる画面）。投稿待ちにしてから、その場で投稿処理を行う。
    /// 一時的なエラーのときは再試行の予定にして返す（予約配信の巡回が自動でやり直す）。
    /// </summary>
    public async Task<PostVariant> PublishNowAsync(Guid variantId, CancellationToken ct)
    {
        RolePolicy.Demand(context.Role, Permission.Schedule);
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == v.ChannelId, ct) ?? throw new NotFoundException("SNS");
        var constraint = PlatformCatalog.Get(v.Platform);
        if (channel.Status != ChannelStatus.Active)
        {
            throw new DomainException(ErrorCodes.SnsReauthRequired, $"{constraint.DisplayName}の再接続が必要です。「SNS連携」から接続し直してください。");
        }
        if (constraint.LinkPolicy == LinkPolicy.DiscouragedByCost && PostText.ContainsUrl(PostText.Compose(v.Body, v.Hashtags)) && !v.UrlCostAcknowledged)
        {
            throw new DomainException(ErrorCodes.PubXUrlCost,
                $"X で URL を含む投稿は費用が高くなります（1件あたり約 ${constraint.CostPerPostWithUrlUsd:0.00}）。確認のチェックを入れてから投稿してください。");
        }
        v.PublishNow(clock.GetUtcNow());
        db.Record(context, "variant.publish_now", nameof(PostVariant), v.Id, null);
        await db.SaveChangesAsync(ct);
        await PublishOneAsync(v.Id, ct);
        await db.ReloadAsync(v, ct);
        return v;
    }

    /// <summary>1件を公開する。戻り値は処理後の状態（他プロセスが処理済みなら null）。</summary>
    public async Task<VariantStatus?> PublishOneAsync(Guid variantId, CancellationToken ct)
    {
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct);
        var now = clock.GetUtcNow();
        if (v is null || !v.IsDue(now)) return null; // 冪等：既に処理済み

        // 分散ロックの代わりに状態遷移を楽観排他で確定する
        v.MarkPublishing();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogInformation("Variant {VariantId} is being published by another worker", variantId);
            return null;
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == v.ChannelId, ct);
        var name = PlatformCatalog.Get(v.Platform).DisplayName;
        try
        {
            if (channel is null || channel.Status != ChannelStatus.Active)
            {
                throw new SocialApiException(ErrorCodes.SnsReauthRequired,
                    $"{name}の再認証が必要です。チャネル設定から再接続してください。", isTransient: false);
            }

            var publisher = publishers.Get(v.Platform, channel.IsDemo);
            var validation = await publisher.ValidateAsync(v, ct);
            if (!validation.IsValid)
            {
                throw new SocialApiException(ErrorCodes.PubFailed, string.Join(" / ", validation.Errors), isTransient: false);
            }

            var credential = await tokens.GetCredentialAsync(channel, ct);
            var media = await MediaForAsync(v, ct);
            var result = await publisher.PublishAsync(v, credential, media, ct);
            v.MarkPublished(result.ExternalPostId, result.Url, clock.GetUtcNow());
            Record(v, "variant.published", result.Url);
            // TODO(F-10): MetricsCollectJob を 1h, 6h, 24h, 72h, 7d, 30d で登録する
        }
        catch (SocialApiException ex) when (ex.IsTransient)
        {
            v.ScheduleRetry(clock.GetUtcNow(), ex.ErrorCode, ex.Message);
            Record(v, v.Status == VariantStatus.Failed ? "variant.publish_failed" : "variant.publish_retry", ex.Message);
            log.LogWarning(ex, "Transient publish error for {VariantId} (attempt {Attempt})", v.Id, v.RetryCount);
        }
        catch (SocialApiException ex)
        {
            if (ex.RequiresReauth && channel is not null) await tokens.MarkReauthAsync(channel, ex.Message, ct);
            v.MarkFailed(ex.ErrorCode, $"{name}への投稿に失敗しました（{ex.Message}）。内容を確認して再実行してください。");
            Record(v, "variant.publish_failed", ex.Message);
            log.LogWarning(ex, "Publish failed for {VariantId}", v.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 想定外の例外は一時的エラーとして扱い、上限まで再試行する
            v.ScheduleRetry(clock.GetUtcNow(), ErrorCodes.SysUnexpected, ex.Message);
            Record(v, "variant.publish_retry", ex.GetType().Name);
            log.LogError(ex, "Unexpected publish error for {VariantId}", v.Id);
        }

        await db.SaveChangesAsync(ct);
        return v.Status;
    }

    /// <summary>
    /// 公開 URL の有効期間。Instagram・Threads・Facebook は投稿時に取得するため短時間でよいが、
    /// LINE は受信者が開いたときに取得されうるため長めにする。
    /// </summary>
    public static TimeSpan MediaUrlLifetime(SocialPlatform platform) =>
        platform == SocialPlatform.Line ? TimeSpan.FromDays(30) : TimeSpan.FromMinutes(30);

    private async Task<IReadOnlyList<PublishMedia>> MediaForAsync(PostVariant v, CancellationToken ct)
    {
        if (v.MediaAssetIds.Count == 0) return [];
        var assets = await db.MediaAssets.Where(m => v.MediaAssetIds.Contains(m.Id)).ToListAsync(ct);
        var ids = assets.Select(a => a.Id).ToList();
        var thumbs = await db.MediaAssets.Where(m => m.ParentAssetId != null && ids.Contains(m.ParentAssetId.Value) && m.DerivationKey == MediaService.ThumbnailKey)
            .ToDictionaryAsync(m => m.ParentAssetId!.Value, ct);

        return v.MediaAssetIds
            .Select(id => assets.FirstOrDefault(a => a.Id == id))
            .Where(a => a is not null)
            .Select(a => new PublishMedia(a!.Id, a.Mime, a.AltText, a.IsAiLabeled,
                async c =>
                {
                    await using var stream = await storage.OpenReadAsync(a.BlobPath, c);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, c);
                    return ms.ToArray();
                },
                c => PublicUrlAsync(a.Id, v.Platform, c),
                c => PublicUrlAsync(thumbs.TryGetValue(a.Id, out var t) ? t.Id : a.Id, v.Platform, c),
                a.SubtitlesSrt))
            .ToList();
    }

    /// <summary>SNS が取得できる公開 HTTPS URL（Blob の SAS、またはアプリ配信 URL）。</summary>
    private async Task<string> PublicUrlAsync(Guid assetId, SocialPlatform platform, CancellationToken ct)
    {
        var url = await signer.CreateReadUrlAsync(assetId, MediaUrlLifetime(platform), ct);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SocialApiException(ErrorCodes.PubFailed,
                "画像の公開URLを作れません。Blob Storage（Media:BlobServiceUri）か、外部から到達できる HTTPS の Media:PublicBaseUrl を設定してください。",
                isTransient: false);
        }
        return url;
    }

    /// <summary>希望公開日時までに承認されなかった投稿を保留にする（F-07-2）。</summary>
    private async Task<int> HoldExpiredApprovalsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.PostVariants
            .Where(v => v.Status == VariantStatus.InReview && v.RequestedPublishAt != null && v.RequestedPublishAt <= now)
            .OrderBy(v => v.RequestedPublishAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var v in expired)
        {
            v.Hold("予約時刻までに承認されなかったため保留にしました。日時を選び直して承認を依頼してください。");
            Record(v, "variant.held", "approval_expired");
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    private void Record(PostVariant v, string action, string? detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = v.TenantId,
            Actor = "system",
            Action = action,
            TargetType = nameof(PostVariant),
            TargetId = v.Id,
            Detail = detail,
        });
}
