using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>A/B テストの判定（1時間ごと）。両方の投稿の公開から72時間たったテストを判定する。</summary>
public sealed class AbTestScheduler(IServiceScopeFactory scopes, ILogger<AbTestScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

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
                var n = await scope.ServiceProvider.GetRequiredService<AbTestService>().EvaluateDueAsync(stoppingToken);
                if (n > 0) log.LogInformation("Evaluated {Count} A/B test(s)", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "A/B test evaluation failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
