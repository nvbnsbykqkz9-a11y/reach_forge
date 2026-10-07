using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// 定期ジョブをプロセス内で動かすエンジン（Jobs:Engine = Hosted）。起動直後に1回実行し、以後は cron の時刻に実行する。
/// 同じジョブは重ならない（前回が終わってから次の時刻を待つ）。失敗時は <see cref="SystemJob.Retries"/> 回まで間隔をあけて再試行する。
/// </summary>
public sealed class HostedJobScheduler(
    SystemJobRunner runner,
    IOptions<JobOptions> options,
    TimeProvider clock,
    ILogger<HostedJobScheduler> log) : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tz = TimeZones.Find(options.Value.TimeZone);
        return Task.WhenAll(SystemJobCatalog.All.Select(job => LoopAsync(job, tz, stoppingToken)));
    }

    private async Task LoopAsync(SystemJob job, TimeZoneInfo tz, CancellationToken ct)
    {
        var cron = CronExpression.Parse(job.Cron);
        try
        {
            await Task.Yield();
            while (!ct.IsCancellationRequested)
            {
                await RunWithRetriesAsync(job, ct);
                var now = clock.GetUtcNow();
                var next = NextOccurrence(cron, now, tz);
                await Task.Delay(next - now, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public static DateTimeOffset NextOccurrence(CronExpression cron, DateTimeOffset now, TimeZoneInfo tz) =>
        cron.GetNextOccurrence(now, tz) ?? now.AddDays(1);

    private async Task RunWithRetriesAsync(SystemJob job, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await runner.RunAsync(job.Id, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt >= job.Retries)
                {
                    log.LogError(ex, "{Job} failed after {Attempts} attempt(s); waiting for the next run", job.Name, attempt + 1);
                    return;
                }
                log.LogWarning(ex, "{Job} failed (attempt {Attempt}); retrying", job.Name, attempt + 1);
                await Task.Delay(RetryDelay * (attempt + 1), clock, ct);
            }
        }
    }
}
