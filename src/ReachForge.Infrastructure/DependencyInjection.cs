using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.AI;
using ReachForge.Application;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Infrastructure.Email;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Jobs;
using ReachForge.Infrastructure.Media;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Infrastructure.Reporting;
using ReachForge.Infrastructure.Web;
using ReachForge.Application.Ai;
using ReachForge.Infrastructure.Security;
using ReachForge.Social;

namespace ReachForge.Infrastructure;

public static class DependencyInjection
{
    /// <summary>SQLite 用のマイグレーションを持つアセンブリ（Database:SqliteMigrations=true のとき使う）。</summary>
    public const string SqliteMigrationsAssembly = "ReachForge.Migrations.Sqlite";

    /// <summary>
    /// アプリケーション・AI・SNS・永続化をまとめて登録する。<see cref="ITenantContext"/> はホスト側（Web / Worker）で登録すること。
    /// </summary>
    public static IServiceCollection AddReachForge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReachForgeApplication();
        services.AddReachForgeAi(configuration);
        services.AddReachForgeSocial(configuration);

        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connection = configuration.GetConnectionString("ReachForge") ?? "Data Source=reachforge.local.db";
        // 設定は要求に依存しないため、オプションはシングルトンにする（プロンプトの保存先など、シングルトンからも短命の DbContext を作るため）
        services.AddDbContext<ReachForgeDbContext>(o =>
        {
            if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(connection);
            // Windows 版は SQLite もマイグレーションで更新する（ReachForge.Migrations.Sqlite）。開発・テストはモデルから作る
            else if (configuration.GetValue("Database:SqliteMigrations", false)) o.UseSqlite(connection, x => x.MigrationsAssembly(SqliteMigrationsAssembly));
            else o.UseSqlite(connection);
        }, optionsLifetime: ServiceLifetime.Singleton);
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<ReachForgeDbContext>());
        services.AddScoped<TenantContextOverride>();

        // 暗号鍵は DB に保存して Web・Worker で共有する（同じ鍵でトークンを復号するため）
        services.AddDataProtection()
            .SetApplicationName("ReachForge")
            .PersistKeysToDbContext<ReachForgeDbContext>();

        // OAuth の state 等の一時保管：複数インスタンスでは Redis
        // 画面へのリアルタイム通知（AI ジョブの進捗・受信箱）：Redis があれば Pub/Sub で Web・Worker 間に届ける
        services.AddSingleton<Realtime.RealtimeBus>();
        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddStackExchangeRedisCache(o => o.Configuration = redis);
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ => StackExchange.Redis.ConnectionMultiplexer.Connect(redis));
            services.AddSingleton<Realtime.RedisRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<Realtime.RedisRealtimeNotifier>());
            services.AddHostedService(sp => sp.GetRequiredService<Realtime.RedisRealtimeNotifier>());
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<IRealtimeNotifier, Realtime.LocalRealtimeNotifier>();
        }
        services.AddScoped<IOAuthStateStore, DistributedOAuthStateStore>();

        // SNS トークン：Key Vault（本番）または DB に暗号化保存
        if (configuration["Secrets:KeyVaultUri"] is { Length: > 0 } vaultUri)
        {
            services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
            services.AddScoped<ICredentialStore, KeyVaultCredentialStore>();
        }
        else
        {
            services.AddScoped<ICredentialStore, EncryptedDbCredentialStore>();
        }

        services.AddScoped<AccountService>();

        // ---- プロンプト管理（RF-DES-001 4.5） ----
        services.AddMemoryCache();
        services.AddSingleton<Prompts.DbPromptStore>();
        services.AddSingleton<IPromptStore>(sp => sp.GetRequiredService<Prompts.DbPromptStore>());
        services.AddSingleton<Prompts.PromptAdminService>();
        // 画面から変える設定（SNS アプリの ID・シークレット）
        services.AddSingleton<Settings.AppSettingsReloader>();
        services.AddSingleton<Settings.AppSettingsService>();

        // ---- ジョブ基盤（14章）：キューへの登録側。処理役はホストが AddReachForgeJobs / AddReachForgeWorkConsumers で登録する ----
        services.AddReachForgeWorkQueue(configuration);
        services.AddScoped<DataRetentionService>();

        // ---- メディア（F-04 / SCR-13） ----
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
        services.Configure<ContentSafetyOptions>(configuration.GetSection(ContentSafetyOptions.SectionName));
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IProvenanceStamper, C2paProvenanceStamper>();
        var media = configuration.GetSection(MediaOptions.SectionName).Get<MediaOptions>() ?? new MediaOptions();
        if (!string.IsNullOrWhiteSpace(media.BlobServiceUri))
        {
            var blobService = new BlobServiceClient(new Uri(media.BlobServiceUri), new DefaultAzureCredential());
            services.AddSingleton(blobService);
            services.AddSingleton(blobService.GetBlobContainerClient(media.BlobContainer));
            services.AddSingleton<IMediaStorage, BlobMediaStorage>();
            services.AddScoped<IMediaUrlSigner, BlobSasUrlSigner>();
        }
        else
        {
            services.AddSingleton<IMediaStorage, LocalMediaStorage>();
            services.AddSingleton<IMediaUrlSigner, AppMediaUrlSigner>();
        }
        if (configuration.GetSection(ContentSafetyOptions.SectionName).Get<ContentSafetyOptions>() is { IsConfigured: true })
        {
            services.AddHttpClient(AzureContentSafetyImageChecker.HttpClientName);
            services.AddScoped<IImageSafetyChecker, AzureContentSafetyImageChecker>();
        }
        else
        {
            services.AddSingleton<IImageSafetyChecker, NotConfiguredImageSafetyChecker>();
        }

        // ---- ショート動画（F-05）：FFmpeg ----
        services.Configure<VideoOptions>(configuration.GetSection(VideoOptions.SectionName));
        services.AddSingleton<BgmLibrary>();
        services.AddSingleton<IBgmLibrary>(sp => sp.GetRequiredService<BgmLibrary>());
        services.AddSingleton<IVideoComposer, FfmpegVideoComposer>();

        // ---- ブランド診断（F-02）：外部サイトの取得。リダイレクトは自前で検査するため自動追従しない ----
        services.AddHttpClient(SafeWebPageFetcher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // 名前解決後の接続先も検査する（DNS リバインディング対策）
                ConnectCallback = async (context, ct) =>
                {
                    var entries = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                    var address = entries.FirstOrDefault(SafeWebPageFetcher.IsPublic)
                                  ?? throw new HttpRequestException("非公開のアドレスには接続できません。");
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            });
        services.AddScoped<IWebPageFetcher, SafeWebPageFetcher>();

        // ---- レポート（F-10）・メール ----
        services.Configure<ReportOptions>(configuration.GetSection(ReportOptions.SectionName));
        services.AddSingleton<IReportPdfRenderer, QuestPdfReportRenderer>();
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        if (configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() is { IsConfigured: true })
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        return services;
    }
}
