using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Guardrails;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Security;

namespace ReachForge.Infrastructure.Persistence;

/// <summary>
/// EF Core のコンテキスト。テナント分離はグローバルクエリフィルタで担保する
/// （本番の PostgreSQL では行レベルセキュリティ（RLS）と二重化する：RF-DES-001 6.2）。
/// </summary>
public sealed class ReachForgeDbContext(
    DbContextOptions<ReachForgeDbContext> options,
    ITenantContext tenant,
    IAiUsageSink? usage = null,
    TimeProvider? clock = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext, IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<MasterPost> MasterPosts => Set<MasterPost>();
    public DbSet<PostVariant> PostVariants => Set<PostVariant>();
    public DbSet<ApprovalAction> ApprovalActions => Set<ApprovalAction>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<AiGeneration> AiGenerations => Set<AiGeneration>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<PostMetric> PostMetrics => Set<PostMetric>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<ChannelMetric> ChannelMetrics => Set<ChannelMetric>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ChannelSecret> ChannelSecrets => Set<ChannelSecret>();

    /// <summary>Data Protection の鍵（Web・Worker で共有。本番は Key Vault の鍵で保護する）。</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // グローバルクエリフィルタから参照する（要求ごとに評価される）
    private Guid CurrentTenantId => tenant.TenantId;
    private bool IsSystem => tenant.IsSystem;

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        if (Database.IsSqlite())
        {
            // SQLite は DateTimeOffset の比較・並べ替えができないため、バイナリ表現（UTC tick）で保存する
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            builder.Properties<decimal>().HaveConversion<double>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity のテーブル
        b.Entity<AppUser>().HasIndex(u => u.TenantId);
        b.Entity<ChannelSecret>().HasIndex(x => x.ChannelId);
        b.Entity<WorkspaceMember>().HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
        b.Entity<WorkspaceMember>().HasIndex(x => x.UserId);
        b.Entity<Invitation>().HasIndex(x => x.TokenHash).IsUnique();

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)).ToList())
        {
            b.Entity(type.ClrType).Property(nameof(Entity.RowVersion)).IsConcurrencyToken();
            b.Entity(type.ClrType).HasIndex(nameof(Entity.TenantId));
            s_applyFilter.MakeGenericMethod(type.ClrType).Invoke(this, [b]);
        }

        b.Entity<Workspace>().Property(x => x.Name).HasMaxLength(200);

        b.Entity<Channel>(e =>
        {
            e.Property(x => x.ExternalAccountId).HasMaxLength(128);
            e.Property(x => x.CredentialSecretRef).HasMaxLength(256);
            e.HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId });
            e.HasIndex(x => x.WorkspaceId);
        });

        b.Entity<BrandProfile>(e =>
        {
            e.HasIndex(x => x.WorkspaceId).IsUnique();
            e.Property(x => x.Tone).HasConversion(Json<BrandTone>());
            e.Property(x => x.Personas).HasConversion(Json<List<Persona>>(), JsonComparer<List<Persona>>());
        });

        b.Entity<PostVariant>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.MasterPostId);
            e.HasIndex(x => new { x.WorkspaceId, x.Status });
            e.Property(x => x.PlatformOptions).HasConversion(Json<Dictionary<string, string>>(),
                JsonComparer<Dictionary<string, string>>());
            e.Property(x => x.GuardrailFindings).HasConversion(Json<List<GuardrailFinding>>(),
                JsonComparer<List<GuardrailFinding>>());
            e.Property(x => x.AbGroup).HasMaxLength(8);
            e.Ignore(x => x.HasGuardrailErrors);
            e.Ignore(x => x.IdempotencyKey);
        });

        b.Entity<PostMetric>().HasIndex(x => new { x.PostVariantId, x.CapturedAt });
        b.Entity<ChannelMetric>().HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
        b.Entity<MediaAsset>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.DerivationKey });
            e.HasIndex(x => x.ParentAssetId);
            e.Ignore(x => x.AspectRatio);
        });
        b.Entity<AiJob>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Ignore(x => x.IsFinished);
        });
        b.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
        b.Entity<AiGeneration>().HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Entity<CreditAccount>().HasIndex(x => x.TenantId).IsUnique();
        b.Entity<Workspace>().Ignore(x => x.RequiresApproval);
        b.Entity<Workspace>().Property(x => x.Reports).HasConversion(Json<ReportSettings>(), JsonComparer<ReportSettings>());
        b.Entity<Report>().HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
    }

    private static readonly MethodInfo s_applyFilter =
        typeof(ReachForgeDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : Entity
    {
        Expression<Func<T, bool>> filter = e => IsSystem || e.TenantId == CurrentTenantId;
        b.Entity<T>().HasQueryFilter(filter);
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => Entry(entity).ReloadAsync(cancellationToken);

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (usage is not null)
        {
            foreach (var log in usage.Drain()) AiUsageLogs.Add(log);
        }

        var now = _clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty && !tenant.IsSystem) entry.Entity.TenantId = tenant.TenantId;
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.RowVersion++;
                    break;
            }

            if (!tenant.IsSystem && entry.State is EntityState.Added or EntityState.Modified &&
                entry.Entity.TenantId != tenant.TenantId)
            {
                throw new InvalidOperationException("他のテナントのデータは更新できません。");
            }
        }
        return await base.SaveChangesAsync(cancellationToken);
    }

    private static ValueConverter<T, string> Json<T>() => new(
        v => JsonSerializer.Serialize(v, s_json),
        v => JsonSerializer.Deserialize<T>(v, s_json)!);

    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, c) => JsonSerializer.Serialize(a, s_json) == JsonSerializer.Serialize(c, s_json),
        v => JsonSerializer.Serialize(v, s_json).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, s_json), s_json)!);
}
