using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record MetricsRunResult(int Posts, int Accounts, int Failed);

/// <summary>
/// 指標の収集（14章 MetricsCollectJob）。公開後 1h〜30d の各チェックポイントで投稿の指標を取得し、
/// アカウントのフォロワー数は1日1回取得する。テナント横断で動作するためシステムコンテキストで実行すること。
/// 要再接続のチャネルは飛ばし、一時的なエラーは次回の実行で取り直す。
/// </summary>
public sealed class MetricsCollectionService(
    IAppDbContext db,
    ITenantContext context,
    IInsightsReaderFactory readers,
    ChannelTokenService tokens,
    TimeProvider clock,
    ILogger<MetricsCollectionService> log)
{
    /// <summary>1回の API 呼び出しで問い合わせる投稿数（X の ids は最大100件）。</summary>
    public const int BatchSize = 50;

    public async Task<MetricsRunResult> CollectDueAsync(CancellationToken ct)
    {
        if (!context.IsSystem) throw new InvalidOperationException("MetricsCollectionService はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();
        var since = now - MetricSchedule.Horizon;

        var published = (await db.PostVariants
                .Where(v => v.Status == VariantStatus.Published && v.ExternalPostId != null && v.PublishedAt != null)
                .Select(v => new { v.Id, v.ChannelId, v.TenantId, v.Platform, v.ExternalPostId, v.PublishedAt })
                .ToListAsync(ct))
            .Where(v => v.PublishedAt >= since)
            .ToList();
        var ids = published.Select(v => v.Id).ToList();
        var lastCaptured = (await db.PostMetrics.Where(m => ids.Contains(m.PostVariantId))
                .Select(m => new { m.PostVariantId, m.CapturedAt }).ToListAsync(ct))
            .GroupBy(m => m.PostVariantId)
            .ToDictionary(g => g.Key, g => g.Max(m => m.CapturedAt));
        var due = published
            .Where(v => MetricSchedule.IsDue(v.PublishedAt!.Value, lastCaptured.TryGetValue(v.Id, out var c) ? c : null, now))
            .ToList();

        int posts = 0, failed = 0;
        var channelIds = due.Select(v => v.ChannelId).Distinct().ToList();
        var channels = await db.Channels.Where(c => channelIds.Contains(c.Id) && c.Status == ChannelStatus.Active).ToListAsync(ct);
        foreach (var channel in channels)
        {
            if (readers.Get(channel.Platform, channel.IsDemo) is not { } reader) continue;
            var targets = due.Where(v => v.ChannelId == channel.Id).ToList();
            try
            {
                var credential = await tokens.GetCredentialAsync(channel, ct);
                foreach (var chunk in targets.Chunk(BatchSize))
                {
                    var snapshots = await reader.GetPostMetricsAsync(chunk.Select(v => v.ExternalPostId!).ToList(), credential, ct);
                    var byExternal = snapshots.ToDictionary(s => s.ExternalPostId);
                    foreach (var v in chunk)
                    {
                        if (!byExternal.TryGetValue(v.ExternalPostId!, out var s)) continue;
                        db.PostMetrics.Add(new PostMetric
                        {
                            TenantId = v.TenantId,
                            PostVariantId = v.Id,
                            Platform = v.Platform,
                            CapturedAt = now,
                            PostedAt = v.PublishedAt!.Value,
                            Impressions = s.Impressions,
                            Reach = s.Reach,
                            Views = s.Views,
                            Likes = s.Likes,
                            Comments = s.Comments,
                            Shares = s.Shares,
                            Saves = s.Saves,
                            LinkClicks = s.LinkClicks,
                            ProfileVisits = s.ProfileVisits,
                            Follows = s.Follows,
                        });
                        posts++;
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (SocialApiException ex)
            {
                // 要再接続は ChannelTokenService がチャネルの状態を更新済み。その他は次回取り直す
                failed += targets.Count;
                log.LogWarning("Metrics collection failed for channel {ChannelId} ({Platform}): {Code} {Message}",
                    channel.Id, channel.Platform, ex.ErrorCode, ex.Message);
                if (ex.RequiresReauth) await tokens.MarkReauthAsync(channel, ex.Message, ct);
            }
        }

        var accounts = await CollectAccountsAsync(now, ct);
        return new MetricsRunResult(posts, accounts, failed);
    }

    /// <summary>アカウントの日次指標（フォロワー数）。その日にまだ取得していないチャネルだけ取得する。</summary>
    private async Task<int> CollectAccountsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var done = await db.ChannelMetrics.Where(m => m.Date == today).Select(m => m.ChannelId).ToListAsync(ct);
        var channels = await db.Channels.Where(c => c.Status == ChannelStatus.Active && !done.Contains(c.Id)).ToListAsync(ct);
        var count = 0;
        foreach (var channel in channels)
        {
            if (readers.Get(channel.Platform, channel.IsDemo) is not { } reader) continue;
            try
            {
                var credential = await tokens.GetCredentialAsync(channel, ct);
                var s = await reader.GetAccountMetricsAsync(today, credential, ct);
                db.ChannelMetrics.Add(new ChannelMetric
                {
                    TenantId = channel.TenantId,
                    WorkspaceId = channel.WorkspaceId,
                    ChannelId = channel.Id,
                    Platform = channel.Platform,
                    Date = today,
                    Followers = s.Followers,
                    Impressions = s.Impressions,
                    ProfileVisits = s.ProfileVisits,
                });
                await db.SaveChangesAsync(ct);
                count++;
            }
            catch (SocialApiException ex)
            {
                log.LogWarning("Account metrics failed for channel {ChannelId} ({Platform}): {Code} {Message}",
                    channel.Id, channel.Platform, ex.ErrorCode, ex.Message);
                if (ex.RequiresReauth) await tokens.MarkReauthAsync(channel, ex.Message, ct);
            }
        }
        return count;
    }
}
