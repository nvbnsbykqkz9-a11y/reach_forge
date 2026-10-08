using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Jobs;

public sealed record RetentionResult(int AuditLogs, int AiJobs, int Invitations)
{
    public int Total => AuditLogs + AiJobs + Invitations;
}

/// <summary>DataRetentionJob（14章：毎日 02:00 JST）。保存期限を過ぎたデータを削除する。</summary>
public sealed class DataRetentionService(ReachForgeDbContext db, ITenantContext tenant, TimeProvider clock)
{
    /// <summary>監査ログは2年保存（RF-DES-001 11章）。</summary>
    public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(731);

    /// <summary>終了した AI ジョブ（生成物は MediaAsset 側に残る）。</summary>
    public static readonly TimeSpan AiJobRetention = TimeSpan.FromDays(90);

    /// <summary>期限切れ・取り消し・承諾済みの招待。</summary>
    public static readonly TimeSpan InvitationRetention = TimeSpan.FromDays(90);

    public async Task<RetentionResult> PurgeAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("PurgeAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();

        var auditBefore = now - AuditRetention;
        var audits = await db.AuditLogs.Where(a => a.CreatedAt < auditBefore).ExecuteDeleteAsync(ct);

        // LP からつくった動画のジョブは、つくったものの画面で参照するため残す
        var jobBefore = now - AiJobRetention;
        var keep = db.LpProjects.Where(p => p.MediaJobId != null).Select(p => p.MediaJobId!.Value);
        var jobs = await db.AiJobs
            .Where(j => (j.Status == AiJobStatus.Succeeded || j.Status == AiJobStatus.Failed || j.Status == AiJobStatus.Canceled)
                && j.CompletedAt != null && j.CompletedAt < jobBefore && !keep.Contains(j.Id))
            .ExecuteDeleteAsync(ct);

        var inviteBefore = now - InvitationRetention;
        var invitations = await db.Invitations.Where(i => i.ExpiresAt < inviteBefore).ExecuteDeleteAsync(ct);

        return new RetentionResult(audits, jobs, invitations);
    }
}
