using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Identity;
using ReachForge.Social.Mock;

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
    /// <summary>起動時の初期化（マイグレーション・プロンプトの初期版・デモデータ）を同時に行わないためのロックの番号。</summary>
    public const long InitializationLockKey = 0x52_46_49_4E_49_54; // "RFINIT"

    /// <summary>
    /// DB の初期化。PostgreSQL では Web・Worker・複数のインスタンスが同時に起動しても1つずつ行うよう、
    /// アドバイザリロック（pg_advisory_lock）を取ってから行う（同時にマイグレーションすると履歴表の重複で失敗するため）。
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<DbContextOptions<ReachForgeDbContext>>();
        var extension = options.Extensions.OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>().FirstOrDefault();
        if (extension?.ConnectionString is not { } connectionString || !options.Extensions.Any(e => e.GetType().Name.StartsWith("Npgsql", StringComparison.Ordinal)))
        {
            await InitializeCoreAsync(services, seed, ct);
            return;
        }

        await using var lockConnection = new Npgsql.NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(ct);
        await using (var acquire = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_lock({InitializationLockKey})", lockConnection))
        {
            await acquire.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await InitializeCoreAsync(services, seed, ct);
        }
        finally
        {
            await using var release = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_unlock({InitializationLockKey})", lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task InitializeCoreAsync(IServiceProvider services, bool seed, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ReachForgeDbContext>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await using var db = new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true }, null, clock);

        if (db.Database.IsNpgsql())
        {
            // PostgreSQL（本番）：EF Core マイグレーション（Expand → Migrate → Contract）。RLS もマイグレーションで適用する。
            // 本番は CI/CD でマイグレーション（所有者ロール）を実行し、アプリは RLS を回避できない専用ロールで接続する
            var migrate = seed || scope.ServiceProvider.GetService<IConfiguration>()
                ?.GetValue("Database:MigrateOnStartup", false) == true;
            if (migrate && (await db.Database.GetPendingMigrationsAsync(ct)).Any()) await db.Database.MigrateAsync(ct);
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
        // プロンプトの初期版（コードの既定テンプレート）を登録する。マイグレーションが未適用なら次回に回す
        if (!db.Database.IsNpgsql() || !(await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            await new Prompts.PromptAdminService(options, scope.ServiceProvider.GetService<Prompts.DbPromptStore>()
                ?? new Prompts.DbPromptStore(options, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), clock), clock).EnsureSeededAsync(ct);
        }
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
            new Campaign
            {
                TenantId = TenantId, WorkspaceId = WorkspaceId, Name = "秋の新作フェア", Code = "autumn2026", Objective = PostObjective.Traffic,
                StartsOn = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-20), EndsOn = DateOnly.FromDateTime(now.UtcDateTime).AddDays(25),
                Kpi = CampaignKpi.LinkClicks, KpiTarget = 800, Budget = 30000m,
                Platforms = [SocialPlatform.X, SocialPlatform.Instagram, SocialPlatform.Threads, SocialPlatform.Line],
                Description = "秋限定メニューの来店を増やす",
            },
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
        SeedInbox(db, channels, now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>FAQ（返信案の根拠）と受信箱のサンプル。</summary>
    private static void SeedInbox(ReachForgeDbContext db, IReadOnlyList<Channel> channels, DateTimeOffset now)
    {
        (string Q, string A, bool Auto)[] faq =
        [
            ("営業時間は何時から何時までですか？", "平日は8:00〜20:00、土日祝は9:00〜18:00です（火曜定休）。", true),
            ("席の予約はできますか？", "4名様以上の席のご予約はお電話（03-0000-0000）で承っています。", false),
            ("テイクアウトはできますか？", "すべてのドリンクと焼き菓子をテイクアウトいただけます。", true),
            ("駐車場はありますか？", "専用の駐車場はありません。近くのコインパーキングをご利用ください。", true),
            ("さつまいもラテはいつから販売しますか？", "秋限定さつまいもラテは10月10日（土）から販売します。", false),
        ];
        foreach (var (q, a, auto) in faq)
        {
            db.KnowledgeEntries.Add(new KnowledgeEntry { TenantId = TenantId, WorkspaceId = WorkspaceId, Question = q, Answer = a, AllowAutoReply = auto });
        }

        (int Minutes, string Author, string Text)[] samples =
        [
            (5, "@yuki_123", "注文したのに届いていません…どうなっていますか？"),
            (12, "@hana_cafe", "ラテは何時から買えますか？"),
            (60, "ゆうこ", "4人で予約できますか？"),
            (95, "@mari.coffee", "写真すてきです！週末に行きます☕"),
            (180, "@aya_m", "テイクアウトはできますか？"),
            (300, "@promo_bot99", "フォロワーを1000人増やします！今すぐDMください"),
            (1500, "@kenta_88", "土曜日は何時まで営業していますか？"),
            (2000, "@sweets_love", "さつまいもラテ、とても美味しかったです！"),
        ];
        var settings = new InboxSettings();
        for (var i = 0; i < samples.Length; i++)
        {
            var (minutes, author, text) = samples[i];
            var channel = channels[i % channels.Count];
            var labels = Domain.Engagement.InboxHeuristics.Classify(text);
            var m = new InboxMessage
            {
                TenantId = TenantId, WorkspaceId = WorkspaceId, ChannelId = channel.Id, Platform = channel.Platform,
                Kind = channel.Platform == SocialPlatform.Line ? InboxKind.DirectMessage : InboxKind.Comment,
                ExternalId = $"seed-inbox-{i}", AuthorId = author, AuthorName = author, Text = text, ReceivedAt = now.AddMinutes(-minutes),
            };
            m.Classify(labels.Sentiment, labels.Intent, labels.Urgency, labels.Sensitive, labels.Language,
                labels.Urgency switch
                {
                    Urgency.High => TimeSpan.FromHours(settings.SlaHighHours),
                    Urgency.Medium => TimeSpan.FromHours(settings.SlaMediumHours),
                    _ => null,
                });
            db.InboxMessages.Add(m);
        }
    }

    /// <summary>過去60日のフォロワー数（日次）。</summary>
    private static void SeedFollowers(ReachForgeDbContext db, IReadOnlyList<Channel> channels, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        for (var c = 0; c < channels.Count; c++)
        {
            for (var d = 60; d >= 1; d--)
            {
                var followers = MockInsightsReader.Followers(channels[c].ExternalAccountId, today.AddDays(-d));
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
                CapturedAt = postedAt.AddDays(7) < now ? postedAt.AddDays(7) : now,
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
