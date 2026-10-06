using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record CalendarEntry(Guid VariantId, Guid MasterPostId, string Title, SocialPlatform Platform,
    string ChannelName, VariantStatus Status, DateTimeOffset At, bool IsAiGenerated);

/// <summary>予約投稿・配信（F-08 / SCR-07）。</summary>
public sealed class SchedulingService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    private static readonly VariantStatus[] s_countsTowardLimit =
        [VariantStatus.Scheduled, VariantStatus.Publishing, VariantStatus.Published];

    public async Task<PostVariant> ScheduleAsync(Guid variantId, DateTimeOffset scheduledAtUtc, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == v.WorkspaceId, ct);
        var channel = await db.Channels.FirstAsync(c => c.Id == v.ChannelId, ct);
        var tz = await TenantTimeZoneAsync(ct);

        if (channel.Status != ChannelStatus.Active)
        {
            throw new DomainException(ErrorCodes.SnsReauthRequired,
                $"{PlatformCatalog.Get(channel.Platform).DisplayName}の再認証が必要です。チャネル設定から再接続してください。");
        }

        var constraint = PlatformCatalog.Get(v.Platform);
        if (constraint.LinkPolicy == LinkPolicy.DiscouragedByCost &&
            PostText.ContainsUrl(PostText.Compose(v.Body, v.Hashtags)) && !v.UrlCostAcknowledged)
        {
            throw new DomainException(ErrorCodes.PubXUrlCost,
                $"X でURLを含む投稿は1件あたりの費用が高くなります（推定 ${constraint.CostPerPostWithUrlUsd:0.00}）。費用を確認のうえ予約してください。");
        }

        await EnsureDailyLimitAsync(v, channel, constraint, scheduledAtUtc, tz, ct);

        v.Schedule(scheduledAtUtc, clock.GetUtcNow(), workspace.RequiresApproval);
        db.Record(tenant, "variant.scheduled", nameof(PostVariant), v.Id, v.ScheduledAt?.ToString("O"));
        await db.SaveChangesAsync(ct);
        return v;
    }

    public async Task UnscheduleAsync(Guid variantId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == v.WorkspaceId, ct);
        v.Unschedule(workspace.RequiresApproval);
        db.Record(tenant, "variant.unscheduled", nameof(PostVariant), v.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task RetryAsync(Guid variantId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        v.RetryNow(clock.GetUtcNow());
        db.Record(tenant, "variant.retry", nameof(PostVariant), v.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid variantId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        v.Cancel();
        db.Record(tenant, "variant.canceled", nameof(PostVariant), v.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>カレンダー表示用の一覧（予約・公開済み・承認待ちの希望日時を含む）。</summary>
    public async Task<IReadOnlyList<CalendarEntry>> CalendarAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken ct)
    {
        var variants = (await db.PostVariants
                .Where(v => v.WorkspaceId == tenant.WorkspaceId && v.Status != VariantStatus.Canceled)
                .ToListAsync(ct))
            .Select(v => (v, At: v.PublishedAt ?? v.ScheduledAt ?? v.RequestedPublishAt))
            .Where(x => x.At is { } at && at >= fromUtc && at < toUtc)
            .ToList();

        var postIds = variants.Select(x => x.v.MasterPostId).Distinct().ToList();
        var posts = await db.MasterPosts.Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var channels = await db.Channels.Where(c => c.WorkspaceId == tenant.WorkspaceId).ToDictionaryAsync(c => c.Id, ct);

        return variants
            .OrderBy(x => x.At)
            .Select(x => new CalendarEntry(x.v.Id, x.v.MasterPostId, posts[x.v.MasterPostId].Title, x.v.Platform,
                channels.TryGetValue(x.v.ChannelId, out var c) ? c.DisplayName : "", x.v.Status, x.At!.Value,
                x.v.AiGenerationId is not null))
            .ToList();
    }

    /// <summary>おすすめ投稿時刻（上位3枠）。</summary>
    public async Task<IReadOnlyList<BestTimeSlot>> BestTimesAsync(Guid? channelId, CancellationToken ct)
    {
        var query = db.PostMetrics.AsQueryable();
        if (channelId is { } id)
        {
            var variantIds = db.PostVariants.Where(v => v.ChannelId == id).Select(v => v.Id);
            query = query.Where(m => variantIds.Contains(m.PostVariantId));
        }
        var since = clock.GetUtcNow().AddDays(-BestTimeCalculator.LookbackDays);
        var latest = (await query.Where(m => m.PostedAt >= since).ToListAsync(ct))
            .GroupBy(m => m.PostVariantId)
            .Select(g => g.MaxBy(m => m.CapturedAt)!);
        return BestTimeCalculator.Suggest(latest, await TenantTimeZoneAsync(ct), clock.GetUtcNow());
    }

    /// <summary>すべての予約を一時停止する（F-08 緊急停止、管理者のみ）。</summary>
    public async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.PauseAllPublishing);
        var t = await db.Tenants.FirstAsync(x => x.Id == tenant.TenantId, ct);
        t.PublishingPaused = paused;
        db.Record(tenant, paused ? "publishing.paused" : "publishing.resumed", nameof(Tenant), t.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsPausedAsync(CancellationToken ct) =>
        await db.Tenants.Where(x => x.Id == tenant.TenantId).Select(x => x.PublishingPaused).FirstOrDefaultAsync(ct);

    public async Task<TimeZoneInfo> TenantTimeZoneAsync(CancellationToken ct)
    {
        var id = await db.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.TimeZoneId).FirstOrDefaultAsync(ct);
        return TimeZoneInfo.TryFindSystemTimeZoneById(id ?? "Asia/Tokyo", out var tz) ? tz : TimeZoneInfo.Utc;
    }

    /// <summary>SNS の日次上限に達する予約は確定時点で拒否する（F-08-6 / E-SNS-020）。</summary>
    private async Task EnsureDailyLimitAsync(PostVariant v, Channel channel, PlatformConstraint constraint,
        DateTimeOffset at, TimeZoneInfo tz, CancellationToken ct)
    {
        if (constraint.DailyPostLimit is not { } limit) return;
        var localDay = TimeZoneInfo.ConvertTime(at, tz).Date;
        var dayStart = new DateTimeOffset(localDay, tz.GetUtcOffset(localDay)).ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);

        var sameDay = (await db.PostVariants
                .Where(x => x.ChannelId == channel.Id && x.Id != v.Id && s_countsTowardLimit.Contains(x.Status))
                .ToListAsync(ct))
            .Count(x => (x.PublishedAt ?? x.ScheduledAt) is { } t && t >= dayStart && t < dayEnd);
        if (sameDay >= limit)
        {
            throw new DomainException(ErrorCodes.SnsDailyLimit,
                $"{constraint.DisplayName}の1日の投稿上限（{limit}件）に達しています。予約日時を変更してください。");
        }
    }
}
