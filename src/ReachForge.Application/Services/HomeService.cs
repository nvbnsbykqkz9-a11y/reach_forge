using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

/// <summary>ホームに出す最近の投稿（SNS 名・状態・SNS 上の URL）。</summary>
public sealed record RecentPost(Guid Id, SocialPlatform Platform, string Account, string Body, VariantStatus Status,
    DateTimeOffset At, string? Url, string? Error);

/// <summary>ホーム（初心者向け）：SNS ごとの連携状況と、最近つくった・投稿した投稿。</summary>
public sealed class HomeService(IAppDbContext db)
{
    public async Task<IReadOnlyList<Channel>> ChannelsAsync(CancellationToken ct) =>
        await db.Channels.AsNoTracking().Where(c => c.Status != ChannelStatus.Revoked).ToListAsync(ct);

    /// <summary>最近の投稿（投稿済み・投稿中・失敗、つくりかけの下書き）。</summary>
    public async Task<IReadOnlyList<RecentPost>> RecentPostsAsync(int count, CancellationToken ct)
    {
        var channels = await db.Channels.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
        var variants = (await db.PostVariants.AsNoTracking()
                .Where(v => v.Status != VariantStatus.Canceled)
                .ToListAsync(ct))
            .OrderByDescending(v => v.PublishedAt ?? v.UpdatedAt)
            .Take(count);
        return variants.Select(v => new RecentPost(v.Id, v.Platform, channels.GetValueOrDefault(v.ChannelId, ""), v.Body, v.Status,
            v.PublishedAt ?? v.UpdatedAt, v.Url, v.LastError)).ToList();
    }
}
