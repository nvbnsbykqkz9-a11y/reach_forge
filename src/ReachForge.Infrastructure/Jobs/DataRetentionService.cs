using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Jobs;

public sealed record RetentionResult(int Idempotency, int AuditLogs, int AiJobs, int TrendIdeas, int Invitations,
    int PostMetricsRolledUp = 0, int PartitionsCreated = 0, int PartitionsDropped = 0)
{
    public int Total => Idempotency + AuditLogs + AiJobs + TrendIdeas + Invitations + PostMetricsRolledUp + PartitionsCreated + PartitionsDropped;
}

/// <summary>DataRetentionJob（14章：毎日 02:00 JST）。保存期限を過ぎたデータを削除する。</summary>
public sealed class DataRetentionService(ReachForgeDbContext db, ITenantContext tenant, TimeProvider clock)
{
    /// <summary>監査ログは2年保存（RF-DES-001 11章）。</summary>
    public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(731);

    /// <summary>終了した AI ジョブ（生成物は MediaAsset・Report 側に残る）。</summary>
    public static readonly TimeSpan AiJobRetention = TimeSpan.FromDays(90);

    /// <summary>使用済み・見送り・古くなったネタ帳。</summary>
    public static readonly TimeSpan TrendRetention = TimeSpan.FromDays(90);

    /// <summary>期限切れ・取り消し・承諾済みの招待。</summary>
    public static readonly TimeSpan InvitationRetention = TimeSpan.FromDays(90);

    /// <summary>投稿指標の明細を残す月数（これより古い月は PostMetricRollups へ移して明細を削除する）。</summary>
    public const int PostMetricDetailMonths = 13;

    /// <summary>先に作っておく月次パーティションの数（PostgreSQL）。</summary>
    public const int PartitionsAhead = 3;

    private const int RollupBatch = 200;

    public async Task<RetentionResult> PurgeAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("PurgeAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();

        var idempotencyBefore = now - IdempotencyRecord.Retention;
        var idempotency = await db.IdempotencyRecords.Where(r => r.CreatedAt < idempotencyBefore).ExecuteDeleteAsync(ct);

        var auditBefore = now - AuditRetention;
        var audits = await db.AuditLogs.Where(a => a.CreatedAt < auditBefore).ExecuteDeleteAsync(ct);

        var jobBefore = now - AiJobRetention;
        var jobs = await db.AiJobs
            .Where(j => (j.Status == AiJobStatus.Succeeded || j.Status == AiJobStatus.Failed || j.Status == AiJobStatus.Canceled)
                && j.CompletedAt != null && j.CompletedAt < jobBefore)
            .ExecuteDeleteAsync(ct);

        var trendBefore = now - TrendRetention;
        var oldDate = DateOnly.FromDateTime(trendBefore.UtcDateTime);
        var ideas = await db.TrendIdeas
            .Where(i => (i.Status != IdeaStatus.New && i.UpdatedAt < trendBefore) || i.RecommendedDate < oldDate)
            .ExecuteDeleteAsync(ct);

        var inviteBefore = now - InvitationRetention;
        var invitations = await db.Invitations.Where(i => i.ExpiresAt < inviteBefore).ExecuteDeleteAsync(ct);

        var cutoff = MetricCutoff(now);
        var rolledUp = await RollupPostMetricsAsync(cutoff, ct);
        var (created, dropped) = await MaintainPartitionsAsync(cutoff, now, ct);

        return new RetentionResult(idempotency, audits, jobs, ideas, invitations, rolledUp, created, dropped);
    }

    /// <summary>明細を残す最初の月（UTC の月初）。これより前に取得した指標を集計へ移す。</summary>
    public static DateTimeOffset MetricCutoff(DateTimeOffset now) =>
        new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-PostMetricDetailMonths);

    /// <summary>
    /// 古い投稿指標を投稿×月の集計（その月の最後の値）へ移し、明細を削除する。投稿ごとに1トランザクションで、
    /// 途中で止まっても二重に数えない（集計と削除を同時に確定する）。
    /// </summary>
    private async Task<int> RollupPostMetricsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var moved = 0;
        while (true)
        {
            var variantIds = await db.PostMetrics.Where(m => m.CapturedAt < cutoff).Select(m => m.PostVariantId).Distinct()
                .Take(RollupBatch).ToListAsync(ct);
            if (variantIds.Count == 0) return moved;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.PostMetrics.AsNoTracking().Where(m => variantIds.Contains(m.PostVariantId) && m.CapturedAt < cutoff).ToListAsync(ct);
            var existing = await db.PostMetricRollups.Where(r => variantIds.Contains(r.PostVariantId)).ToListAsync(ct);
            foreach (var group in rows.GroupBy(m => (m.PostVariantId, Month: new DateOnly(m.CapturedAt.UtcDateTime.Year, m.CapturedAt.UtcDateTime.Month, 1))))
            {
                var last = group.MaxBy(m => m.CapturedAt)!;
                var rollup = existing.FirstOrDefault(r => r.PostVariantId == group.Key.PostVariantId && r.Month == group.Key.Month);
                if (rollup is null)
                {
                    rollup = new PostMetricRollup
                    {
                        TenantId = last.TenantId, PostVariantId = last.PostVariantId, Platform = last.Platform, Month = group.Key.Month,
                        LastCapturedAt = DateTimeOffset.MinValue,
                    };
                    db.PostMetricRollups.Add(rollup);
                    existing.Add(rollup);
                }
                rollup.Absorb(last, group.Count());
            }
            await db.SaveChangesAsync(ct);
            moved += await db.PostMetrics.Where(m => variantIds.Contains(m.PostVariantId) && m.CapturedAt < cutoff).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>PostgreSQL：先の月のパーティションを作り、明細を移し終えた古いパーティションを削除する。</summary>
    private async Task<(int Created, int Dropped)> MaintainPartitionsAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return (0, 0);
        var thisMonth = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        var created = await db.Database.SqlQuery<int>(
                $"SELECT rf_ensure_post_metric_partitions({thisMonth}, {thisMonth.AddMonths(PartitionsAhead)}) AS \"Value\"")
            .SingleAsync(ct);
        var dropped = await db.Database.SqlQuery<int>($"SELECT rf_drop_post_metric_partitions({DateOnly.FromDateTime(cutoff.UtcDateTime)}) AS \"Value\"")
            .SingleAsync(ct);
        return (created, dropped);
    }
}
