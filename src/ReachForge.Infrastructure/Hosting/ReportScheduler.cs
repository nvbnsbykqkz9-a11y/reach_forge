using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>
/// WeeklyReportJob／MonthlyReportJob（14章）。15分ごとに、定期作成が有効なワークスペースの作成時刻
/// （月曜 07:00／毎月1日 07:00、テナントのタイムゾーン）を過ぎたかを確認し、AI ジョブとして登録する。
/// </summary>
public sealed class ReportScheduler(IServiceScopeFactory scopes, ILogger<ReportScheduler> log) : BackgroundService
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
                var created = await scope.ServiceProvider.GetRequiredService<ReportService>().ScheduleDueAsync(stoppingToken);
                if (created > 0) log.LogInformation("Scheduled {Count} periodic report(s)", created);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Report scheduling failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
