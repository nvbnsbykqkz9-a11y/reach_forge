using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
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
    ICredentialStore credentials,
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

            var publisher = publishers.Get(v.Platform);
            var validation = await publisher.ValidateAsync(v, ct);
            if (!validation.IsValid)
            {
                throw new SocialApiException(ErrorCodes.PubFailed, string.Join(" / ", validation.Errors), isTransient: false);
            }

            var credential = await credentials.GetAsync(channel, ct);
            var result = await publisher.PublishAsync(v, credential, ct);
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

    /// <summary>希望公開日時までに承認されなかった投稿を保留にする（F-07-2）。</summary>
    private async Task<int> HoldExpiredApprovalsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.PostVariants
            .Where(v => v.Status == VariantStatus.InReview && v.RequestedPublishAt != null && v.RequestedPublishAt <= now)
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
