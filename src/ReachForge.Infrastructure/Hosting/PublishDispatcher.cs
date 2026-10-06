using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Hosting;

public sealed class PublishDispatcherOptions
{
    public const string SectionName = "Worker";

    /// <summary>予約キューを確認する間隔。予約時刻 ±60 秒以内の実行精度（RF-DES-001 8.1）を満たすよう 60 秒未満にする。</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Web プロセス内でも配信を実行する（ローカル開発で1プロセス起動するため）。</summary>
    public bool RunInWeb { get; set; }
}

/// <summary>
/// 予約投稿の配信ループ（14章 PublishJob）。
/// 初期実装は定期ポーリング＋楽観排他で二重投稿を防ぐ。大量配信時は Hangfire 遅延ジョブ＋Service Bus へ置き換える。
/// </summary>
public sealed class PublishDispatcher(
    IServiceScopeFactory scopes,
    IOptions<PublishDispatcherOptions> options,
    ILogger<PublishDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
                    new MutableTenantContext { IsSystem = true, UserName = "system" };
                var result = await scope.ServiceProvider.GetRequiredService<PublishingService>().RunDueAsync(stoppingToken);
                if (result.Published + result.Retrying + result.Failed + result.Held > 0)
                {
                    log.LogInformation("Publish run: {Published} published, {Retrying} retrying, {Failed} failed, {Held} held",
                        result.Published, result.Retrying, result.Failed, result.Held);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Publish dispatcher run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
