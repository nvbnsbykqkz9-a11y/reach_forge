using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>
/// TokenRefreshJob（14章：期限7日以内のトークン更新）。設計上は毎日 03:00 JST だが、
/// 初期実装は6時間ごとに実行する（Hangfire 導入時に定時ジョブへ移行）。
/// </summary>
public sealed class TokenRefreshScheduler(IServiceScopeFactory scopes, ILogger<TokenRefreshScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
                    new MutableTenantContext { IsSystem = true, UserName = "system" };
                var (refreshed, reauth) = await scope.ServiceProvider.GetRequiredService<ChannelTokenService>()
                    .RefreshExpiringAsync(stoppingToken);
                if (refreshed + reauth > 0)
                {
                    log.LogInformation("Token refresh: {Refreshed} refreshed, {Reauth} need reconnection", refreshed, reauth);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Token refresh run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
