using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.AI;
using ReachForge.Application;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Infrastructure.Security;
using ReachForge.Social;

namespace ReachForge.Infrastructure;

public static class DependencyInjection
{
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
        services.AddDbContext<ReachForgeDbContext>(o =>
        {
            if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(connection);
            else o.UseSqlite(connection);
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<ReachForgeDbContext>());
        services.AddScoped<TenantContextOverride>();

        // 暗号鍵は DB に保存して Web・Worker で共有する（同じ鍵でトークンを復号するため）
        services.AddDataProtection()
            .SetApplicationName("ReachForge")
            .PersistKeysToDbContext<ReachForgeDbContext>();

        // OAuth の state 等の一時保管：複数インスタンスでは Redis
        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddStackExchangeRedisCache(o => o.Configuration = redis);
        }
        else
        {
            services.AddDistributedMemoryCache();
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
        return services;
    }
}
