using Cronos;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Jobs;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

/// <summary>ジョブ基盤（14章）：定期ジョブ・Hangfire・キュー・データ保存期限・AI の利用料金の集計。</summary>
public class JobTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZones.Find("Asia/Tokyo");

    /// <summary>Hangfire のログ設定は static。ほかのテストで破棄されたホストのロガーを使わないよう、何も出力しないものに戻す。</summary>
    private sealed class SilentLogProvider : Hangfire.Logging.ILogProvider, Hangfire.Logging.ILog
    {
        public Hangfire.Logging.ILog GetLogger(string name) => this;

        public bool Log(Hangfire.Logging.LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null) => false;
    }

    public JobTests() => Hangfire.Logging.LogProvider.SetCurrentLogProvider(new SilentLogProvider());

    [Fact]
    public async Task Ai_cost_is_summed_per_month_in_tenant_time_zone()
    {
        await using var f = await AppFixture.CreateAsync();
        var jst = TimeSpan.FromHours(9);
        foreach (var (at, task, provider, usd) in new[]
        {
            (new DateTimeOffset(2026, 10, 31, 12, 0, 0, jst), AiTaskType.Copy, "anthropic", 9m),      // 先々月は数えない
            (new DateTimeOffset(2026, 11, 30, 23, 0, 0, jst), AiTaskType.Copy, "anthropic", 0.10m),   // 先月
            (new DateTimeOffset(2026, 12, 1, 0, 30, 0, jst), AiTaskType.Copy, "anthropic", 0.20m),   // 今月（UTC ではまだ11月）
            (new DateTimeOffset(2026, 12, 5, 12, 0, 0, jst), AiTaskType.Tts, "openai", 0.05m),
            (new DateTimeOffset(2026, 12, 6, 12, 0, 0, jst), AiTaskType.Copy, "local", 0m),
        })
        {
            f.Clock.SetUtcNow(at); // 記録の時刻は保存した時刻になる
            await using var scope = f.Scope();
            var db = f.Get<IAppDbContext>(scope);
            db.AiUsageLogs.Add(new AiUsageLog { TaskType = task, Provider = provider, CostUsd = usd, Succeeded = true });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        f.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 7, 9, 0, 0, jst));
        await using (var scope = f.Scope())
        {
            var summary = await f.Get<AiCostService>(scope).SummaryAsync(CancellationToken.None);
            Assert.Equal(new DateOnly(2026, 12, 1), summary.ThisMonth.Month);
            Assert.Equal(0.25m, summary.ThisMonth.TotalUsd);
            Assert.Equal(3, summary.ThisMonth.Calls);
            Assert.Equal(["広告文・投稿文", "ナレーション"], summary.ThisMonth.ByFeature.Select(l => l.Label));
            Assert.Equal(0.20m, summary.ThisMonth.ByFeature[0].Usd);
            Assert.Equal(["Anthropic（Claude）", "OpenAI", "お試し（料金なし）"], summary.ThisMonth.ByService.Select(l => l.Label));
            Assert.Equal(0.10m, summary.LastMonth.TotalUsd);
            Assert.Equal(1, summary.LastMonth.Calls);
        }
    }

    [Fact]
    public async Task Data_retention_deletes_only_expired_rows_and_keeps_lp_videos()
    {
        await using var f = await AppFixture.CreateAsync();
        Guid oldAudit, oldJob, runningJob, lpJob;
        await using (var scope = f.Scope())
        {
            var db = f.Get<ReachForgeDbContext>(scope);
            var audit = new AuditLog { TenantId = DemoSeeder.TenantId, Actor = "田中", Action = "test.old" };
            AiJob Done()
            {
                var job = new AiJob { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, TaskType = AiTaskType.Image };
                job.Start(f.Clock.GetUtcNow());
                job.Succeed([], f.Clock.GetUtcNow());
                return job;
            }
            var done = Done();
            var video = Done();
            var running = new AiJob { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, TaskType = AiTaskType.Image };
            running.Start(f.Clock.GetUtcNow());
            var project = new LpProject
            {
                TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, Url = "https://example.com", VideoJobId = video.Id,
            };
            db.AddRange(audit, done, video, running, project);
            await db.SaveChangesAsync();
            (oldAudit, oldJob, runningJob, lpJob) = (audit.Id, done.Id, running.Id, video.Id);
        }

        f.Clock.Advance(TimeSpan.FromDays(800));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            var result = await f.Get<DataRetentionService>(worker).PurgeAsync(CancellationToken.None);
            Assert.Equal(1, result.AiJobs);
            Assert.True(result.AuditLogs >= 1);
            Assert.True(result.Invitations >= 0);
        }

        await using (var scope = f.Scope(c => c.IsSystem = true))
        {
            var db = f.Get<ReachForgeDbContext>(scope);
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Id == oldAudit));
            Assert.False(await db.AiJobs.AnyAsync(j => j.Id == oldJob));
            Assert.True(await db.AiJobs.AnyAsync(j => j.Id == runningJob)); // 終わっていないジョブは残す
            Assert.True(await db.AiJobs.AnyAsync(j => j.Id == lpJob)); // つくったものの画面で使う動画のジョブは残す
        }
    }

    [Fact]
    public async Task Every_system_job_runs_in_system_context()
    {
        await using var f = await AppFixture.CreateAsync();
        var runner = new SystemJobRunner(f.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SystemJobRunner>.Instance);
        foreach (var job in SystemJobCatalog.All)
        {
            await runner.RunAsync(job.Id, CancellationToken.None);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public void Cron_schedules_follow_japan_time()
    {
        var retention = SystemJobCatalog.Find("data-retention")!;
        var next = Infrastructure.Jobs.HostedJobScheduler.NextOccurrence(CronExpression.Parse(retention.Cron),
            new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(9)), Tokyo);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.FromHours(9)), next);

        Assert.Equal(SystemJobCatalog.All.Count, SystemJobCatalog.All.Select(j => j.Id).Distinct().Count());
    }

    [Fact]
    public async Task Hangfire_registers_recurring_jobs_in_japan_time_and_removes_stale_ones()
    {
        var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("legacy-job", () => Console.WriteLine("old"), Cron.Daily());
        var registrar = new HangfireRecurringJobRegistrar(manager, storage, Options.Create(new JobOptions()));
        await registrar.StartAsync(CancellationToken.None);

        using var connection = storage.GetConnection();
        var jobs = connection.GetRecurringJobs();
        Assert.Equal(SystemJobCatalog.All.Select(j => j.Id).Order(), jobs.Select(j => j.Id).Order());
        Assert.All(jobs, j => Assert.Equal(Tokyo.Id, j.TimeZoneId));
        Assert.Equal("0 2 * * *", jobs.Single(j => j.Id == "data-retention").Cron);
    }

    [Fact]
    public void Hangfire_retry_counts_follow_the_design()
    {
        new ServiceCollection().AddReachForgeHangfire(new ConfigurationBuilder().Build(), server: false);
        int Attempts(string id) => JobFilterProviders.Providers
            .GetFilters(Job.FromExpression<HangfireSystemJob>(j => j.RunAsync(id, CancellationToken.None)))
            .Select(f => f.Instance).OfType<AutomaticRetryAttribute>().Single().Attempts;

        Assert.Equal(0, Attempts("data-retention"));
    }

    [Fact]
    public async Task Hangfire_server_runs_a_system_job_through_dependency_injection()
    {
        await using var f = await AppFixture.CreateAsync(
            configure: s => s.AddReachForgeHangfire(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:Engine"] = "Hangfire" }).Build(), server: true));
        f.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 0, 5, 0, TimeSpan.FromHours(9)));
        var storage = f.Services.GetRequiredService<JobStorage>();
        using var server = new BackgroundJobServer(
            new BackgroundJobServerOptions { Activator = f.Services.GetRequiredService<JobActivator>(),
                SchedulePollingInterval = TimeSpan.FromMilliseconds(200), WorkerCount = 1 }, storage);

        var id = new BackgroundJobClient(storage).Enqueue<HangfireSystemJob>(j => j.RunAsync("data-retention", CancellationToken.None));
        var monitor = storage.GetMonitoringApi();
        for (var i = 0; i < 100 && monitor.JobDetails(id).History.FirstOrDefault()?.StateName is not ("Succeeded" or "Failed"); i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal("Succeeded", monitor.JobDetails(id).History.First().StateName);
    }

    private sealed class FlakyHandler(string queue, int failures) : IWorkHandler
    {
        public string Queue => queue;
        public int Calls;
        public readonly List<string> Handled = [];

        public Task HandleAsync(string payload, CancellationToken ct)
        {
            if (++Calls <= failures) throw new InvalidOperationException("boom");
            Handled.Add(payload);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task In_process_queue_retries_then_dead_letters_and_can_requeue()
    {
        InProcessWorkConsumer.BaseRetryDelay = TimeSpan.FromMilliseconds(1);
        var flaky = new FlakyHandler("test-queue", failures: 2);
        var broken = new FlakyHandler(WorkQueues.AiJobs, failures: WorkQueues.MaxDeliveryCount);
        var queue = new InProcessWorkQueue([flaky, broken], TimeProvider.System);
        var consumer = new InProcessWorkConsumer(queue, [flaky, broken], TimeProvider.System, NullLogger<InProcessWorkConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync("test-queue", "w1", CancellationToken.None);
            await queue.EnqueueAsync(WorkQueues.AiJobs, "a1", CancellationToken.None);
            await queue.EnqueueAsync("unknown", "x", CancellationToken.None); // 処理役のいないキューは捨てる

            for (var i = 0; i < 200 && (flaky.Handled.Count == 0 || (await queue.PeekAsync(WorkQueues.AiJobs, 10, default)).Count == 0); i++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(["w1"], flaky.Handled);
            Assert.Equal(3, flaky.Calls);
            var dead = Assert.Single(await queue.PeekAsync(WorkQueues.AiJobs, 10, CancellationToken.None));
            Assert.Equal(("a1", "boom"), (dead.Payload, dead.Reason));
            Assert.Equal(WorkQueues.MaxDeliveryCount, broken.Calls);

            // 原因を直して再投入
            Assert.Equal(1, await queue.RequeueAsync(WorkQueues.AiJobs, CancellationToken.None));
            for (var i = 0; i < 200 && broken.Handled.Count == 0; i++) await Task.Delay(10);
            Assert.Equal(["a1"], broken.Handled);
            Assert.Empty(await queue.PeekAsync(WorkQueues.AiJobs, 10, CancellationToken.None));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            InProcessWorkConsumer.BaseRetryDelay = TimeSpan.FromSeconds(1);
        }
    }

    private sealed class RecordingQueue : IWorkQueue
    {
        public readonly List<(string Queue, string Payload)> Messages = [];

        public Task EnqueueAsync(string queue, string payload, CancellationToken ct)
        {
            Messages.Add((queue, payload));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Queued_ai_jobs_are_announced_and_run_from_the_queue_message()
    {
        var recording = new RecordingQueue();
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWorkQueue>(recording));
        AiJob job;
        await using (var scope = f.Scope())
        {
            job = await f.Get<MediaService>(scope).EnqueueGenerationAsync(new ImageJobRequest { Prompt = "秋の新作ラテ" }, CancellationToken.None);
        }
        var message = Assert.Single(recording.Messages);
        Assert.Equal((WorkQueues.AiJobs, job.Id.ToString()), message);

        await new AiJobWorkHandler(f.Services.GetRequiredService<IServiceScopeFactory>()).HandleAsync(message.Payload, CancellationToken.None);
        await using (var scope = f.Scope())
        {
            Assert.Equal(AiJobStatus.Succeeded, (await f.Get<IAppDbContext>(scope).AiJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).Status);
        }
        // 再配信されても二重に実行しない
        await new AiJobWorkHandler(f.Services.GetRequiredService<IServiceScopeFactory>()).HandleAsync(message.Payload, CancellationToken.None);
    }

    [Fact]
    public async Task Service_bus_queue_is_selected_by_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Queue"] = "ServiceBus",
            ["Jobs:ServiceBus:ConnectionString"] = "Endpoint=sb://rf-test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=eA==",
        }).Build();

        // Web（ジョブを動かさない）：登録だけ。処理役は Worker 側
        var web = new ServiceCollection().AddLogging();
        web.AddReachForge(config);
        web.AddReachForgeWorkConsumers(config, all: false);
        await using (var sp = web.BuildServiceProvider())
        {
            Assert.IsType<ServiceBusWorkQueue>(sp.GetRequiredService<IWorkQueue>());
            Assert.IsType<ServiceBusWorkQueue>(sp.GetRequiredService<IDeadLetterAdmin>());
            Assert.Empty(sp.GetServices<IWorkHandler>());
        }

        // Worker：AI ジョブを Service Bus から受け取る
        var worker = new ServiceCollection().AddLogging();
        worker.AddReachForge(config);
        worker.AddReachForgeWorkConsumers(config, all: true);
        Assert.Contains(worker, d => d.ImplementationType == typeof(ServiceBusWorkConsumer));
        await using (var sp = worker.BuildServiceProvider())
        {
            Assert.Equal([WorkQueues.AiJobs], sp.GetServices<IWorkHandler>().Select(h => h.Queue));
        }

        // 接続先がなければ起動時に分かるようにする
        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Queue"] = "ServiceBus" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddReachForge(missing));
    }
}
