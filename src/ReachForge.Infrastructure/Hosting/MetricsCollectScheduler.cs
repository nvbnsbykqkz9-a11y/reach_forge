using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>
/// MetricsCollectJob（14章）。15分ごとに、チェックポイント（公開後 1h〜30d）に達した投稿の指標と、
/// その日に未取得のアカウント指標を取得する。Hangfire 導入時に定時ジョブへ移行する。
/// </summary>
public sealed class MetricsCollectScheduler(IServiceScopeFactory scopes, ILogger<MetricsCollectScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

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
                var result = await scope.ServiceProvider.GetRequiredService<MetricsCollectionService>().CollectDueAsync(stoppingToken);
                if (result.Posts + result.Accounts + result.Failed > 0)
                {
                    log.LogInformation("Metrics: {Posts} posts, {Accounts} accounts, {Failed} failed",
                        result.Posts, result.Accounts, result.Failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Metrics collection run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
