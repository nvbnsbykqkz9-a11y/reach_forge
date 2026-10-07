using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// Hangfire から呼ばれる定期ジョブ（Jobs:Engine = Hangfire）。再試行回数は <see cref="SystemJobRetryFilterProvider"/> がジョブごとに決める。
/// 複数の Worker が同じジョブを同時に動かさないよう、ストレージの分散ロックを取ってから実行する。
/// </summary>
public sealed class HangfireSystemJob(SystemJobRunner runner, JobStorage storage, ILogger<HangfireSystemJob> log)
{
    [JobDisplayName("{0}")]
    public async Task RunAsync(string jobId, CancellationToken cancellationToken)
    {
        IDisposable? held;
        using var connection = storage.GetConnection();
        try
        {
            held = connection.AcquireDistributedLock($"reachforge:job:{jobId}", TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            log.LogInformation("{Job} is already running; skipped", jobId);
            return;
        }
        using (held)
        {
            await runner.RunAsync(jobId, cancellationToken);
        }
    }
}

/// <summary>14章の再試行回数（例：TokenRefreshJob 3回、TrendResearchJob 1回、DataRetentionJob は次周期）を Hangfire に伝える。</summary>
public sealed class SystemJobRetryFilterProvider : IJobFilterProvider
{
    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        if (job?.Type != typeof(HangfireSystemJob) || job.Args.Count == 0 || job.Args[0] is not string id
            || SystemJobCatalog.Find(id) is not { } definition)
        {
            return [];
        }
        return
        [
            new JobFilter(new AutomaticRetryAttribute
            {
                Attempts = definition.Retries,
                OnAttemptsExceeded = AttemptsExceededAction.Fail, // 失敗のまま残し、ダッシュボードから確認・再実行できるようにする
            }, JobFilterScope.Method, null),
        ];
    }
}

/// <summary>定期ジョブを Hangfire に登録する（起動のたびに定義へ合わせる。廃止したジョブは削除する）。</summary>
public sealed class HangfireRecurringJobRegistrar(IRecurringJobManager manager, JobStorage storage, IOptions<JobOptions> options)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var tz = CreditResetService.FindTimeZone(options.Value.TimeZone);
        foreach (var job in SystemJobCatalog.All)
        {
            manager.AddOrUpdate<HangfireSystemJob>(job.Id, j => j.RunAsync(job.Id, CancellationToken.None), job.Cron,
                new RecurringJobOptions { TimeZone = tz });
        }
        using var connection = storage.GetConnection();
        foreach (var stale in connection.GetRecurringJobs().Where(r => SystemJobCatalog.Find(r.Id) is null))
        {
            manager.RemoveIfExists(stale.Id);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class HangfireSetup
{
    private static readonly Lock s_filterLock = new();
    private static bool s_filterAdded;

    /// <summary>
    /// Hangfire のストレージを登録する（メモリ。プロセス間で共有されないため、ジョブは1つのプロセスで動かす）。
    /// </summary>
    /// <param name="server">このプロセスでジョブを実行する（Worker）。false はダッシュボード表示だけ（Web）。</param>
    public static IServiceCollection AddReachForgeHangfire(this IServiceCollection services, IConfiguration configuration, bool server)
    {
        var options = configuration.GetSection(JobOptions.SectionName).Get<JobOptions>() ?? new JobOptions();
        lock (s_filterLock)
        {
            if (!s_filterAdded)
            {
                JobFilterProviders.Providers.Add(new SystemJobRetryFilterProvider());
                s_filterAdded = true;
            }
        }

        services.AddHangfire(c =>
        {
            c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();
            c.UseInMemoryStorage(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.FromHours(6) });
        });
        if (server)
        {
            services.AddHangfireServer(o =>
            {
                o.WorkerCount = Math.Max(1, options.Hangfire.WorkerCount);
                o.ServerName = $"reachforge:{Environment.MachineName}";
            });
            services.TryAddSingleton<SystemJobRunner>();
            services.AddTransient<HangfireSystemJob>();
            services.AddHostedService<HangfireRecurringJobRegistrar>();
        }
        return services;
    }
}
