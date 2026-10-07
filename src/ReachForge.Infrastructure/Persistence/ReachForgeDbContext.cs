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
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Infrastructure.Identity;

namespace ReachForge.Infrastructure.Persistence;

/// <summary>
/// EF Core のコンテキスト（SQLite）。テナント分離はグローバルクエリフィルタで担保する。
/// </summary>
public sealed class ReachForgeDbContext(
    DbContextOptions<ReachForgeDbContext> options,
    ITenantContext tenant,
    IAiUsageSink? usage = null,
    TimeProvider? clock = null,
    IRealtimeNotifier? realtime = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext, IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<AiGeneration> AiGenerations => Set<AiGeneration>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LpProject> LpProjects => Set<LpProject>();

    /// <summary>Data Protection の鍵（Web・Worker で共有。本番は Key Vault の鍵で保護する）。</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // グローバルクエリフィルタから参照する（要求ごとに評価される）
    private Guid CurrentTenantId => tenant.TenantId;
    private bool IsSystem => tenant.IsSystem;

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite は DateTimeOffset の比較・並べ替えができないため、バイナリ表現（UTC tick）で保存する
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<decimal>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity のテーブル
        b.Entity<AppUser>().HasIndex(u => u.TenantId);
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

        b.Entity<BrandProfile>(e =>
        {
            e.HasIndex(x => x.WorkspaceId).IsUnique();
            e.Property(x => x.Tone).HasConversion(Json<BrandTone>());
            e.Property(x => x.Personas).HasConversion(Json<List<Persona>>(), JsonComparer<List<Persona>>());
            e.Property(x => x.FewShotExamples).HasConversion(Json<List<FewShotExample>>(), JsonComparer<List<FewShotExample>>());
        });

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
        b.Entity<AiUsageLog>().HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Entity<PromptTemplate>(e =>
        {
            e.HasIndex(x => new { x.Key, x.Version }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Evaluation).HasConversion(Json<PromptEvalResult?>());
        });
        b.Entity<LpProject>(e =>
        {
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.Platforms).HasConversion(Json<List<SocialPlatform>>(), JsonComparer<List<SocialPlatform>>());
            e.Property(x => x.SourceImageAssetIds).HasConversion(Json<List<Guid>>(), JsonComparer<List<Guid>>());
            e.Property(x => x.Outputs).HasConversion(Json<Dictionary<SocialPlatform, LpPlatformOutput>>(),
                JsonComparer<Dictionary<SocialPlatform, LpPlatformOutput>>());
        });
        b.Entity<AppSetting>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(AppSetting.MaxKeyLength);
            e.Property(x => x.Value).HasMaxLength(AppSetting.MaxValueLength);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });
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
        var events = realtime is null ? [] : RealtimeEvents();
        var saved = await base.SaveChangesAsync(cancellationToken);
        foreach (var e in events) realtime!.Publish(e);
        return saved;
    }

    /// <summary>保存後に画面へ知らせる出来事（AI ジョブの段階・状態の変化、受信箱の更新）。</summary>
    private List<RealtimeEvent> RealtimeEvents()
    {
        var events = new List<RealtimeEvent>();
        foreach (var entry in ChangeTracker.Entries<AiJob>())
        {
            var changed = entry.State == EntityState.Added
                || (entry.State == EntityState.Modified
                    && (entry.Property(j => j.Status).IsModified || entry.Property(j => j.Stage).IsModified));
            if (!changed) continue;
            var j = entry.Entity;
            events.Add(new JobProgressEvent(j.TenantId, j.WorkspaceId, j.Id, j.TaskType, j.Status, j.Stage));
        }
        return events;
    }

    private static ValueConverter<T, string> Json<T>() => new(
        v => JsonSerializer.Serialize(v, s_json),
        v => JsonSerializer.Deserialize<T>(v, s_json)!);

    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, c) => JsonSerializer.Serialize(a, s_json) == JsonSerializer.Serialize(c, s_json),
        v => JsonSerializer.Serialize(v, s_json).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, s_json), s_json)!);
}
