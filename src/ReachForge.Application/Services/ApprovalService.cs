using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record ApprovalQueueItem(PostVariant Variant, MasterPost Post, Channel Channel, string RequestedBy);

/// <summary>承認ワークフロー（F-07 / SCR-08）。</summary>
public sealed class ApprovalService(
    IAppDbContext db,
    ITenantContext tenant,
    IBrandContextProvider brand,
    IApprovalSummaryService summaries,
    TimeProvider clock)
{
    /// <summary>承認待ち一覧（期限が近い順）。</summary>
    public async Task<IReadOnlyList<ApprovalQueueItem>> QueueAsync(CancellationToken ct)
    {
        var variants = await db.PostVariants
            .Where(v => v.WorkspaceId == tenant.WorkspaceId && v.Status == VariantStatus.InReview)
            .ToListAsync(ct);
        return await ToItemsAsync(variants, ct);
    }

    public async Task<IReadOnlyList<ApprovalAction>> HistoryAsync(Guid variantId, CancellationToken ct) =>
        await db.ApprovalActions.Where(a => a.PostVariantId == variantId).OrderBy(a => a.CreatedAt).ToListAsync(ct);

    public Task<IReadOnlyList<string>> SummaryAsync(Guid variantId, CancellationToken ct) =>
        summaries.SummarizeAsync(variantId, ct);

    /// <summary>承認を依頼する。申請前にガードレールを再検査する。</summary>
    public async Task SubmitAsync(IReadOnlyList<Guid> variantIds, DateTimeOffset? requestedPublishAt, string? comment,
        CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.SubmitForApproval);
        var variants = await db.PostVariants.Where(v => variantIds.Contains(v.Id)).ToListAsync(ct);
        foreach (var v in variants)
        {
            var post = await db.MasterPosts.FirstAsync(p => p.Id == v.MasterPostId, ct);
            var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);
            v.ApplyGuardrail(StudioService.CheckVariant(v, ctx.ToGuardrailContext()));
            v.Submit(requestedPublishAt);
            AddAction(v, ApprovalDecision.Submitted, comment);
            db.Record(tenant, "variant.submitted", nameof(PostVariant), v.Id);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 承認する。希望公開日時が未来なら自動で予約する。
    /// <paramref name="exceptionReason"/> 付きの例外承認は Owner のみ（監査ログに記録）。
    /// </summary>
    public async Task ApproveAsync(Guid variantId, string? comment, string? exceptionReason, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Approve);
        var v = await FindAsync(variantId, ct);
        var asException = !string.IsNullOrWhiteSpace(exceptionReason);
        if (asException && tenant.Role != Role.Owner)
        {
            throw new ForbiddenException("例外としての承認はオーナーのみ行えます。");
        }

        v.Approve(asException, exceptionReason);
        AddAction(v, asException ? ApprovalDecision.ExceptionApproved : ApprovalDecision.Approved,
            asException ? $"[例外承認] {exceptionReason} {comment}".Trim() : comment);
        db.Record(tenant, asException ? "variant.exception_approved" : "variant.approved", nameof(PostVariant), v.Id,
            exceptionReason);

        var now = clock.GetUtcNow();
        if (v.RequestedPublishAt is { } at && at > now)
        {
            v.Schedule(at, now, requiresApproval: true);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>まとめて承認する。確認事項（警告・エラー）が0件の投稿のみ対象（RF-UX-001 SCR-08）。</summary>
    public async Task<int> BulkApproveAsync(IReadOnlyList<Guid> variantIds, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Approve);
        var variants = await db.PostVariants
            .Where(v => variantIds.Contains(v.Id) && v.Status == VariantStatus.InReview)
            .ToListAsync(ct);
        var now = clock.GetUtcNow();
        var approved = 0;
        foreach (var v in variants.Where(v => v.GuardrailLevel <= GuardrailLevel.Info))
        {
            v.Approve();
            AddAction(v, ApprovalDecision.Approved, "まとめて承認");
            db.Record(tenant, "variant.approved", nameof(PostVariant), v.Id, "bulk");
            if (v.RequestedPublishAt is { } at && at > now) v.Schedule(at, now, requiresApproval: true);
            approved++;
        }
        await db.SaveChangesAsync(ct);
        return approved;
    }

    public async Task RejectAsync(Guid variantId, string reason, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Approve);
        var v = await FindAsync(variantId, ct);
        v.Reject(reason);
        AddAction(v, ApprovalDecision.Rejected, reason);
        db.Record(tenant, "variant.rejected", nameof(PostVariant), v.Id, reason);
        await db.SaveChangesAsync(ct);
    }

    public async Task CommentAsync(Guid variantId, string comment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new DomainException(ErrorCodes.Validation, "コメントを入力してください。");
        }
        var v = await FindAsync(variantId, ct);
        AddAction(v, ApprovalDecision.Commented, comment);
        db.Record(tenant, "variant.commented", nameof(PostVariant), v.Id);
        await db.SaveChangesAsync(ct);
    }

    internal async Task<IReadOnlyList<ApprovalQueueItem>> ToItemsAsync(List<PostVariant> variants, CancellationToken ct)
    {
        var postIds = variants.Select(v => v.MasterPostId).Distinct().ToList();
        var channelIds = variants.Select(v => v.ChannelId).Distinct().ToList();
        var variantIds = variants.Select(v => v.Id).ToList();
        var posts = await db.MasterPosts.Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var channels = await db.Channels.Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var submitters = (await db.ApprovalActions
                .Where(a => variantIds.Contains(a.PostVariantId) && a.Decision == ApprovalDecision.Submitted)
                .ToListAsync(ct))
            .GroupBy(a => a.PostVariantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAt).Last().ActorName);

        return variants
            .OrderBy(v => v.RequestedPublishAt ?? DateTimeOffset.MaxValue)
            .ThenBy(v => v.CreatedAt)
            .Select(v => new ApprovalQueueItem(v, posts[v.MasterPostId], channels[v.ChannelId],
                submitters.GetValueOrDefault(v.Id, posts[v.MasterPostId].CreatedBy)))
            .ToList();
    }

    private async Task<PostVariant> FindAsync(Guid id, CancellationToken ct) =>
        await db.PostVariants.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("投稿");

    private void AddAction(PostVariant v, ApprovalDecision decision, string? comment) =>
        db.ApprovalActions.Add(new ApprovalAction
        {
            TenantId = v.TenantId,
            PostVariantId = v.Id,
            Decision = decision,
            ActorName = tenant.UserName,
            Comment = comment,
        });
}
