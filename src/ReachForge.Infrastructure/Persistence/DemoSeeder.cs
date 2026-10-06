using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Credits;
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

    /// <summary>DB を作成し、空ならデモデータを投入する。</summary>
    public static async Task InitializeAsync(IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ReachForgeDbContext>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await using var db = new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true }, null, clock);

        // TODO: 本番（PostgreSQL）は EF Core マイグレーション（Expand → Migrate → Contract）で管理する
        await db.Database.EnsureCreatedAsync(ct);
        if (seed && !await SchemaIsCurrentAsync(db, ct))
        {
            // 開発用 DB（デモデータ）のみ：スキーマが古ければ作り直す。本番はマイグレーションで更新する
            await db.Database.EnsureDeletedAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
            await SchemaIsCurrentAsync(db, ct); // 新しいハッシュを記録する
        }
        if (!seed || await db.Tenants.AnyAsync(ct)) return;

        await SeedAsync(db, clock.GetUtcNow(), ct);

        // 認証基盤が登録されているホスト（Web）ではデモ用のログイン利用者も作る
        if (scope.ServiceProvider.GetService<UserManager<AppUser>>() is { } users)
        {
            foreach (var (email, name, role) in DemoUsers)
            {
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

        var workspace = new Workspace { TenantId = TenantId, Name = "ほっこりカフェ 渋谷店", BrandColor = "#B45309", ApprovalSteps = 1 };
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

        db.Campaigns.AddRange(
            new Campaign { TenantId = TenantId, WorkspaceId = WorkspaceId, Name = "秋の新作フェア", Code = "autumn2026", Objective = PostObjective.Traffic },
            new Campaign { TenantId = TenantId, WorkspaceId = WorkspaceId, Name = "インフルエンサー協業（広告）", Code = "collab-ad", IsAdvertisement = true });

        var channels = new[] { SocialPlatform.X, SocialPlatform.Instagram, SocialPlatform.Threads, SocialPlatform.Line }
            .Select(p => new Channel
            {
                TenantId = TenantId,
                WorkspaceId = WorkspaceId,
                Platform = p,
                ExternalAccountId = $"demo-{p.ToString().ToLowerInvariant()}",
                DisplayName = p == SocialPlatform.Line ? "ほっこりカフェ（LINE公式）" : "@hokkori_cafe",
                CredentialSecretRef = "demo://",
                TokenExpiresAt = p == SocialPlatform.Instagram ? now.AddDays(5) : now.AddDays(60),
                Scopes = ["publish", "read_insights"],
                LastCheckedAt = now,
                IsDemo = true,
            })
            .ToList();
        db.Channels.AddRange(channels);

        db.CreditAccounts.Add(CreditAccount.Open(TenantId, 1500, new DateOnly(today.Year, today.Month, 1)));

        SeedHistory(db, channels, now);
        SeedFollowers(db, channels, now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>過去60日のフォロワー数（日次）。</summary>
    private static void SeedFollowers(ReachForgeDbContext db, IReadOnlyList<Channel> channels, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        for (var c = 0; c < channels.Count; c++)
        {
            var followers = 800 + c * 450;
            for (var d = 60; d >= 1; d--)
            {
                followers += (d * 7 + c * 3) % 9;
                db.ChannelMetrics.Add(new ChannelMetric
                {
                    TenantId = TenantId, WorkspaceId = WorkspaceId, ChannelId = channels[c].Id, Platform = channels[c].Platform,
                    Date = today.AddDays(-d), Followers = followers,
                });
            }
        }
    }

    /// <summary>過去60日の公開済み投稿と指標（ダッシュボード・最適時刻提案の計算用）。木曜19時・水曜12時が強い傾向にする。</summary>
    private static void SeedHistory(ReachForgeDbContext db, IReadOnlyList<Channel> channels, DateTimeOffset now)
    {
        var random = new Random(42);
        var jst = TimeSpan.FromHours(9);
        var hours = new[] { 8, 12, 15, 19, 21 };
        var topics = new[] { "新メニュー予告", "スタッフ紹介", "週末クーポン", "雨の日割引", "ラテアートの日", "モーニング再開" };

        for (var i = 0; i < 48; i++)
        {
            var daysAgo = 2 + i * 58 / 48;
            var hour = hours[random.Next(hours.Length)];
            var localDate = now.ToOffset(jst).Date.AddDays(-daysAgo);
            var postedAt = new DateTimeOffset(localDate.AddHours(hour), jst).ToUniversalTime();
            var channel = channels[i % channels.Count];

            var master = new MasterPost
            {
                TenantId = TenantId, WorkspaceId = WorkspaceId, Title = topics[i % topics.Length],
                CoreMessage = $"{topics[i % topics.Length]}のお知らせです。", CreatedBy = "seed",
            };
            db.MasterPosts.Add(master);

            var variant = PostVariant.Create(master, channel, master.CoreMessage, ["ほっこりカフェ"]);
            variant.Schedule(postedAt, postedAt.AddMinutes(-1), requiresApproval: false);
            variant.MarkPublishing();
            variant.MarkPublished($"seed-{i}", null, postedAt);
            db.PostVariants.Add(variant);

            var boost = (localDate.DayOfWeek, hour) switch
            {
                (DayOfWeek.Thursday, 19) => 1.7,
                (DayOfWeek.Wednesday, 12) => 1.4,
                (_, 19) => 1.2,
                _ => 1.0,
            };
            var impressions = random.Next(800, 2600);
            var engagements = (int)(impressions * (0.035 + random.NextDouble() * 0.02) * boost);
            db.PostMetrics.Add(new PostMetric
            {
                TenantId = TenantId,
                PostVariantId = variant.Id,
                Platform = channel.Platform,
                PostedAt = postedAt,
                CapturedAt = postedAt.AddDays(7),
                Impressions = impressions,
                Reach = (long)(impressions * 0.8),
                Likes = engagements * 7 / 10,
                Comments = engagements / 10,
                Shares = engagements / 10,
                Saves = engagements / 10,
                LinkClicks = random.Next(5, 60),
                Follows = random.Next(0, 9),
            });
        }
    }
}
