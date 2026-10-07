using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Realtime;
using StackExchange.Redis;

namespace ReachForge.Application.Tests;

/// <summary>リアルタイム通知（RF-DES-001 3.3）：保存時の出来事の発行と、Redis によるプロセス間の配信。</summary>
public class RealtimeTests
{
    private sealed class Recorder : IRealtimeNotifier
    {
        public readonly ConcurrentQueue<RealtimeEvent> Events = new();
        public void Publish(RealtimeEvent e) => Events.Enqueue(e);
    }

    [Fact]
    public async Task Ai_job_stages_are_published_after_each_save()
    {
        var recorder = new Recorder();
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IRealtimeNotifier>(recorder));
        Guid jobId;
        await using (var scope = f.Scope())
        {
            jobId = (await f.Get<MediaService>(scope).EnqueueGenerationAsync(new ImageJobRequest { Prompt = "ラテ" }, CancellationToken.None)).Id;
        }
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }

        var job = recorder.Events.OfType<JobProgressEvent>().Where(e => e.JobId == jobId).ToList();
        Assert.Equal(AiJobStatus.Queued, job.First().Status);
        Assert.Contains(job, e => e.Status == AiJobStatus.Running);
        Assert.Equal((AiJobStatus.Succeeded, AiJobStage.Done), (job.Last().Status, job.Last().Stage));
        Assert.All(job, e => Assert.Equal(Infrastructure.Persistence.DemoSeeder.WorkspaceId, e.WorkspaceId));
    }

    public static string? Redis => Environment.GetEnvironmentVariable("RF_TEST_REDIS");

    [Fact]
    public async Task Redis_relays_events_between_processes_without_echo()
    {
        Assert.SkipWhen(Redis is null, "RF_TEST_REDIS が未設定");
        using var redisA = await ConnectionMultiplexer.ConnectAsync(Redis!);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(Redis!);
        var busA = new RealtimeBus(NullLogger<RealtimeBus>.Instance);
        var busB = new RealtimeBus(NullLogger<RealtimeBus>.Instance);
        var worker = new RedisRealtimeNotifier(redisA, busA, NullLogger<RedisRealtimeNotifier>.Instance); // Worker
        var web = new RedisRealtimeNotifier(redisB, busB, NullLogger<RedisRealtimeNotifier>.Instance);    // Web
        await worker.StartAsync(CancellationToken.None);
        await web.StartAsync(CancellationToken.None);

        var atWeb = new ConcurrentQueue<RealtimeEvent>();
        var atWorker = new ConcurrentQueue<RealtimeEvent>();
        using var _ = busB.Subscribe(e => { atWeb.Enqueue(e); return Task.CompletedTask; });
        using var __ = busA.Subscribe(e => { atWorker.Enqueue(e); return Task.CompletedTask; });

        var sent = new JobProgressEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiTaskType.Video, AiJobStatus.Running, AiJobStage.Generating);
        worker.Publish(sent);
        for (var i = 0; i < 100 && atWeb.IsEmpty; i++) await Task.Delay(20);
        await Task.Delay(200);

        Assert.Equal(sent, Assert.Single(atWeb));      // 別プロセスへ届く（型も復元される）
        Assert.Equal(sent, Assert.Single(atWorker));   // 自分には1回だけ（Redis からの折り返しは捨てる）
        await worker.StopAsync(CancellationToken.None);
        await web.StopAsync(CancellationToken.None);
    }
}
