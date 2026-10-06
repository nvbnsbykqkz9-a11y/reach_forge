using Microsoft.EntityFrameworkCore;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Abstractions;

/// <summary>永続化の抽象。実装（EF Core）は Infrastructure 層。テナント分離はグローバルクエリフィルタで担保する。</summary>
public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Workspace> Workspaces { get; }
    DbSet<Channel> Channels { get; }
    DbSet<BrandProfile> BrandProfiles { get; }
    DbSet<Product> Products { get; }
    DbSet<Campaign> Campaigns { get; }
    DbSet<MasterPost> MasterPosts { get; }
    DbSet<PostVariant> PostVariants { get; }
    DbSet<ApprovalAction> ApprovalActions { get; }
    DbSet<MediaAsset> MediaAssets { get; }
    DbSet<AiGeneration> AiGenerations { get; }
    DbSet<AiUsageLog> AiUsageLogs { get; }
    DbSet<PostMetric> PostMetrics { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<CreditAccount> CreditAccounts { get; }
    DbSet<WorkspaceMember> WorkspaceMembers { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<AiJob> AiJobs { get; }
    DbSet<ChannelMetric> ChannelMetrics { get; }
    DbSet<Report> Reports { get; }
    DbSet<InboxMessage> InboxMessages { get; }
    DbSet<KnowledgeEntry> KnowledgeEntries { get; }
    DbSet<InboxAlert> InboxAlerts { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>追跡中のエンティティを DB の最新値で読み直す（Blazor のサーキットで他プロセスの更新を反映するため）。</summary>
    Task ReloadAsync(object entity, CancellationToken cancellationToken = default);
}
