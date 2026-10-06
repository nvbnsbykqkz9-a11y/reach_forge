using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>InboxPollJob（14章：5〜15分ごと）。チャネルごとの間隔は InboxService.PollInterval で判定する。</summary>
public sealed class InboxPollScheduler(IServiceScopeFactory scopes, ILogger<InboxPollScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

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
                var r = await scope.ServiceProvider.GetRequiredService<InboxService>().PollDueAsync(stoppingToken);
                if (r.Ingested + r.Failed > 0)
                {
                    log.LogInformation("Inbox poll: {Ingested} new from {Channels} channel(s), {Auto} handled automatically, {Failed} failed",
                        r.Ingested, r.Channels, r.AutoHandled, r.Failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Inbox poll failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
