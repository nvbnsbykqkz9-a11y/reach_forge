using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Identity;

namespace ReachForge.Infrastructure.Persistence;

/// <summary>
/// ローカル開発用のデモデータ。認証基盤（Entra External ID）導入前は、このテナント・ワークスペースで動作する。
/// </summary>
public static class DemoSeeder
{
    public static readonly Guid TenantId = Guid.Parse("01929f00-0000-7000-8000-000000000001");
    public static readonly Guid WorkspaceId = Guid.Parse("01929f00-0000-7000-8000-000000000002");

    /// <summary>開発用ログイン（Development 環境でのみ作成）。本番では使わない。</summary>
    public const string DemoPassword = "ReachForge#2026";

    public static readonly (string Email, string Name, Role Role)[] DemoUsers =
    [
        ("owner@example.com", "田中", Role.Owner),
        ("editor@example.com", "佐々木", Role.Editor),
        ("approver@example.com", "高橋", Role.Approver),
        ("viewer@example.com", "鈴木", Role.Viewer),
    ];

    /// <summary>DB を作成（または新しい版にそろえ）、空ならデモデータを投入する。</summary>
    public static Task InitializeAsync(IServiceProvider services, bool seed, CancellationToken ct = default) =>
        InitializeCoreAsync(services, seed, ct);

    private static async Task InitializeCoreAsync(IServiceProvider services, bool seed, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ReachForgeDbContext>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await using var db = new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true }, null, clock);

        if (scope.ServiceProvider.GetService<IConfiguration>()?.GetValue("Database:SqliteMigrations", false) == true)
        {
            // SQLite（Windows 版）：利用者の PC の DB をマイグレーションで新しい版にそろえる（データは残す）
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            // SQLite（ローカル開発・テスト）：モデルから作成する
            await db.Database.EnsureCreatedAsync(ct);
            if (seed && !await SchemaIsCurrentAsync(db, ct))
            {
                // 開発用 DB（デモデータ）のみ：スキーマが古ければ作り直す
                await db.Database.EnsureDeletedAsync(ct);
                await db.Database.EnsureCreatedAsync(ct);
                await SchemaIsCurrentAsync(db, ct); // 新しいハッシュを記録する
            }
        }
        // プロンプトの初期版（コードの既定テンプレート）を登録する
        await new Prompts.PromptAdminService(options, scope.ServiceProvider.GetService<Prompts.DbPromptStore>()
            ?? new Prompts.DbPromptStore(options, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), clock), clock).EnsureSeededAsync(ct);
        if (!seed) return;
        if (!await db.Tenants.AnyAsync(ct)) await SeedAsync(db, clock.GetUtcNow(), ct);

        // 認証基盤が登録されているホスト（Web）ではデモ用のログイン利用者も作る。
        // Worker が先にデモデータを作った場合も、Web の起動時に足りない利用者を作る
        if (scope.ServiceProvider.GetService<UserManager<AppUser>>() is { } users
            && await db.Tenants.AnyAsync(t => t.Id == TenantId, ct))
        {
            foreach (var (email, name, role) in DemoUsers)
            {
                if (await users.FindByEmailAsync(email) is not null) continue;
                var user = new AppUser
                {
                    UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, TenantId = TenantId,
                    LastWorkspaceId = WorkspaceId, CreatedAt = clock.GetUtcNow(),
                };
                var result = await users.CreateAsync(user, DemoPassword);
                if (!result.Succeeded) continue;
                db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    TenantId = TenantId, WorkspaceId = WorkspaceId, UserId = user.Id, Email = email, DisplayName = name, Role = role,
                });
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// 開発用 DB のスキーマが現在のモデルと同じか。作成スクリプトのハッシュを専用テーブルに記録して比べる
    /// （モデルを変えるたびに判定用のコードを足さなくてよいように）。
    /// </summary>
    private static async Task<bool> SchemaIsCurrentAsync(ReachForgeDbContext db, CancellationToken ct)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript())));
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS rf_schema (hash VARCHAR(64) NOT NULL)", ct);
        var stored = await db.Database.SqlQueryRaw<string>("SELECT hash AS \"Value\" FROM rf_schema").ToListAsync(ct);
        if (stored.Count == 0 && !await db.Tenants.AnyAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("INSERT INTO rf_schema (hash) VALUES ({0})", [hash], ct);
            return true;
        }
        return stored.Count == 1 && stored[0] == hash;
    }

    private static async Task SeedAsync(ReachForgeDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var tenant = new Tenant { Name = "デモ株式会社", TimeZoneId = "Asia/Tokyo", MaxChannels = 10 };
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(tenant, TenantId);
        tenant.TenantId = TenantId;
        db.Tenants.Add(tenant);

        var workspace = new Workspace { TenantId = TenantId, Name = "ほっこりカフェ 渋谷店", BrandColor = "#B45309" };
        typeof(Workspace).GetProperty(nameof(Workspace.Id))!.SetValue(workspace, WorkspaceId);
        db.Workspaces.Add(workspace);

        db.BrandProfiles.Add(new BrandProfile
        {
            TenantId = TenantId,
            WorkspaceId = WorkspaceId,
            BrandName = "ほっこりカフェ",
            Industry = "飲食（カフェ）",
            WebsiteUrl = "https://example.com/hokkori-cafe",
            Tone = new BrandTone { Casualness = 60, FirstPerson = "私たち", EmojiLevel = 2, EndingRule = "「〜です」「〜ますね」で親しみやすく" },
            Personas =
            [
                new Persona { Name = "近所で働く会社員", AgeRange = "25〜39歳", Interests = "季節限定メニュー、テイクアウト", Pains = "昼休みが短い" },
            ],
            NgWords = ["激安", "コスパ最強"],
            BrandColors = ["#B45309", "#FDE68A", "#7C2D12"],
            PreferredHashtags = ["ほっこりカフェ", "渋谷カフェ"],
        });

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        db.Products.AddRange(
            new Product
            {
                TenantId = TenantId, WorkspaceId = WorkspaceId, Name = "秋限定さつまいもラテ", Price = 580,
                Description = "北海道産さつまいもを使った、ほっくり甘いラテ。", AvailableFrom = today.AddDays(4),
                AvailableUntil = today.AddDays(60), Url = "https://example.com/hokkori-cafe/menu/latte",
            },
            new Product
            {
                TenantId = TenantId, WorkspaceId = WorkspaceId, Name = "かぼちゃのチーズケーキ", Price = 520,
                Description = "濃厚なかぼちゃとクリームチーズのケーキ。", AvailableFrom = today, Url = "https://example.com/hokkori-cafe/menu/cake",
            });

        await db.SaveChangesAsync(ct);
    }
}
