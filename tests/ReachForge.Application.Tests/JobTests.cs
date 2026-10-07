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

/// <summary>ジョブ基盤（14章）：定期ジョブ・Hangfire・キュー・クレジットの月次付与・データ保存期限。</summary>
public class JobTests
{
    private static readonly TimeZoneInfo Tokyo = SchedulingService.FindTimeZone("Asia/Tokyo");

    /// <summary>Hangfire のログ設定は static。ほかのテストで破棄されたホストのロガーを使わないよう、何も出力しないものに戻す。</summary>
    private sealed class SilentLogProvider : Hangfire.Logging.ILogProvider, Hangfire.Logging.ILog
    {
        public Hangfire.Logging.ILog GetLogger(string name) => this;

        public bool Log(Hangfire.Logging.LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null) => false;
    }

    public JobTests() => Hangfire.Logging.LogProvider.SetCurrentLogProvider(new SilentLogProvider());

    [Fact]
    public async Task Credit_reset_grants_once_per_month_in_tenant_time_zone_and_keeps_holds()
    {
        await using var f = await AppFixture.CreateAsync();
        await using (var scope = f.Scope())
        {
            await f.Get<ICreditService>(scope).ReserveAsync(10, CancellationToken.None);
            var db = f.Get<IAppDbContext>(scope);
            var account = await db.CreditAccounts.SingleAsync();
            account.Commit(0, 100); // 今月 100 消費
            await db.SaveChangesAsync();
        }

        // 10/31 23:30 JST：まだ10月
        f.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(9)));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            Assert.Equal(0, await f.Get<CreditResetService>(worker).ResetDueAsync(CancellationToken.None));
        }

        // 11/1 00:05 JST（UTC ではまだ10月31日）
        f.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 0, 5, 0, TimeSpan.FromHours(9)));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            var service = f.Get<CreditResetService>(worker);
            Assert.Equal(1, await service.ResetDueAsync(CancellationToken.None));
            Assert.Equal(0, await service.ResetDueAsync(CancellationToken.None)); // 冪等
        }

        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var account = await db.CreditAccounts.AsNoTracking().SingleAsync();
            Assert.Equal(new DateOnly(2026, 11, 1), account.PeriodStart);
            Assert.Equal(account.MonthlyGrant, account.Balance);
            Assert.Equal(0, account.ConsumedThisPeriod);
            Assert.Equal(10, account.Held); // 実行中のジョブの分は残す
            Assert.Single(await db.AuditLogs.AsNoTracking().Where(a => a.Action == "credits.reset").ToListAsync());
        }
    }

    [Fact]
    public async Task Data_retention_deletes_only_expired_rows()
    {
        await using var f = await AppFixture.CreateAsync();
        Guid oldAudit, oldIdea, newIdea, oldJob, runningJob;
        await using (var scope = f.Scope())
        {
            var db = f.Get<ReachForgeDbContext>(scope);
            var audit = new AuditLog { TenantId = DemoSeeder.TenantId, Actor = "田中", Action = "test.old" };
            var idea = new TrendIdea { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, Topic = "古いネタ",
                RecommendedDate = new DateOnly(2026, 10, 10), Status = IdeaStatus.Used };
            var done = new AiJob { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, TaskType = AiTaskType.Image };
            done.Start(f.Clock.GetUtcNow());
            done.Succeed([], 0, f.Clock.GetUtcNow());
            var running = new AiJob { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, TaskType = AiTaskType.Image };
            running.Start(f.Clock.GetUtcNow());
            db.AddRange(audit, idea, done, running,
                new IdempotencyRecord { TenantId = DemoSeeder.TenantId, Scope = "s", Key = "old", RequestHash = "h" });
            await db.SaveChangesAsync();
            (oldAudit, oldIdea, oldJob, runningJob) = (audit.Id, idea.Id, done.Id, running.Id);
        }

        f.Clock.Advance(TimeSpan.FromDays(800));
        await using (var scope = f.Scope())
        {
            var db = f.Get<ReachForgeDbContext>(scope);
            var idea = new TrendIdea { TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, Topic = "新しいネタ",
                RecommendedDate = DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime) };
            db.AddRange(idea, new IdempotencyRecord { TenantId = DemoSeeder.TenantId, Scope = "s", Key = "new", RequestHash = "h" });
            await db.SaveChangesAsync();
            newIdea = idea.Id;
        }

        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            var result = await f.Get<DataRetentionService>(worker).PurgeAsync(CancellationToken.None);
            Assert.Equal(1, result.Idempotency);
            Assert.Equal(1, result.AiJobs);
            Assert.Equal(1, result.TrendIdeas);
            Assert.True(result.AuditLogs >= 1);
            Assert.True(result.Invitations >= 0);
        }

        await using (var scope = f.Scope(c => c.IsSystem = true))
        {
            var db = f.Get<ReachForgeDbContext>(scope);
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Id == oldAudit));
            Assert.False(await db.TrendIdeas.AnyAsync(i => i.Id == oldIdea));
            Assert.True(await db.TrendIdeas.AnyAsync(i => i.Id == newIdea));
            Assert.False(await db.AiJobs.AnyAsync(j => j.Id == oldJob));
            Assert.True(await db.AiJobs.AnyAsync(j => j.Id == runningJob)); // 終わっていないジョブは残す
            Assert.Equal("new", (await db.IdempotencyRecords.SingleAsync()).Key);
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

        var tokens = SystemJobCatalog.Find("token-refresh")!;
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero), // 03:00 JST
            Infrastructure.Jobs.HostedJobScheduler.NextOccurrence(CronExpression.Parse(tokens.Cron),
                new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), Tokyo));
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

        Assert.Equal(3, Attempts("token-refresh"));
        Assert.Equal(3, Attempts("metrics-collect"));
        Assert.Equal(2, Attempts("report-schedule"));
        Assert.Equal(1, Attempts("trend-research"));
        Assert.Equal(0, Attempts("credit-reset"));
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

        var id = new BackgroundJobClient(storage).Enqueue<HangfireSystemJob>(j => j.RunAsync("credit-reset", CancellationToken.None));
        var monitor = storage.GetMonitoringApi();
        for (var i = 0; i < 100 && monitor.JobDetails(id).History.FirstOrDefault()?.StateName is not ("Succeeded" or "Failed"); i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal("Succeeded", monitor.JobDetails(id).History.First().StateName);

        await using var scope = f.Scope();
        Assert.Equal(new DateOnly(2026, 11, 1), (await f.Get<IAppDbContext>(scope).CreditAccounts.AsNoTracking().SingleAsync()).PeriodStart);
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
        var flaky = new FlakyHandler(WorkQueues.Webhooks, failures: 2);
        var broken = new FlakyHandler(WorkQueues.AiJobs, failures: WorkQueues.MaxDeliveryCount);
        var queue = new InProcessWorkQueue([flaky, broken], TimeProvider.System);
        var consumer = new InProcessWorkConsumer(queue, [flaky, broken], TimeProvider.System, NullLogger<InProcessWorkConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(WorkQueues.Webhooks, "w1", CancellationToken.None);
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

        // Worker：Webhook と AI ジョブを Service Bus から受け取る
        var worker = new ServiceCollection().AddLogging();
        worker.AddReachForge(config);
        worker.AddReachForgeWorkConsumers(config, all: true);
        Assert.Contains(worker, d => d.ImplementationType == typeof(ServiceBusWorkConsumer));
        await using (var sp = worker.BuildServiceProvider())
        {
            Assert.Equal([WorkQueues.Webhooks, WorkQueues.AiJobs], sp.GetServices<IWorkHandler>().Select(h => h.Queue));
        }

        // 接続先がなければ起動時に分かるようにする
        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Queue"] = "ServiceBus" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddReachForge(missing));
    }
}
