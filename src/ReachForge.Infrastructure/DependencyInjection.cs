using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.AI;
using ReachForge.Application;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
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
        services.AddSingleton<ICredentialStore, DevCredentialStore>();
        services.AddScoped<TenantContextOverride>();
        return services;
    }
}
