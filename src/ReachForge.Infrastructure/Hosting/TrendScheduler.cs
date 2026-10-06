using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>TrendResearchJob（14章：毎日 06:00、テナントのタイムゾーン）。30分ごとに確認し、その日の分がなければ更新する。</summary>
public sealed class TrendScheduler(IServiceScopeFactory scopes, ILogger<TrendScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

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
                var n = await scope.ServiceProvider.GetRequiredService<TrendService>().RefreshDueAsync(stoppingToken);
                if (n > 0) log.LogInformation("Trend ideas refreshed: {Count}", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Trend refresh failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
