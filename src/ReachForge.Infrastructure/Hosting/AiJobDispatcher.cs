using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Jobs;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>
/// AI ジョブ（画像・動画・レポート）の巡回（14章 AiGenerationJob）。通常はキュー（<see cref="AiJobWorkHandler"/>）で実行し、
/// ここでは通知が届かなかった待機中のジョブと、止まったジョブ（FailStale）を拾う。
/// 各ジョブは依頼したテナント・ワークスペースのコンテキストで実行する。
/// </summary>
public sealed class AiJobDispatcher(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<AiJobDispatcher> log)
    : BackgroundService
{
    public const int BatchSize = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.AiSweepInterval);
        do
        {
            try
            {
                await RunOnceAsync(scopes, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "AI job dispatcher run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>待機中のジョブを最大 <see cref="BatchSize"/> 件実行する（テストからも呼ぶ）。</summary>
    public static async Task<int> RunOnceAsync(IServiceScopeFactory scopes, CancellationToken ct)
    {
        List<AiJob> queued;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
                new MutableTenantContext { IsSystem = true, UserName = "system" };
            var db = scope.ServiceProvider.GetRequiredService<ReachForgeDbContext>();
            await scope.ServiceProvider.GetRequiredService<AiJobProcessor>().FailStaleAsync(ct);
            queued = (await db.AiJobs.AsNoTracking().Where(j => j.Status == AiJobStatus.Queued).ToListAsync(ct))
                .OrderBy(j => j.CreatedAt).Take(BatchSize).ToList();
        }

        foreach (var job in queued) await RunAsync(scopes, job, ct);
        return queued.Count;
    }

    /// <summary>1件実行する（キューの通知から）。待機中でなければ何もしない。</summary>
    public static async Task RunJobAsync(IServiceScopeFactory scopes, Guid jobId, CancellationToken ct)
    {
        AiJob? job;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
                new MutableTenantContext { IsSystem = true, UserName = "system" };
            job = await scope.ServiceProvider.GetRequiredService<ReachForgeDbContext>().AiJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        }
        if (job is { Status: AiJobStatus.Queued }) await RunAsync(scopes, job, ct);
    }

    private static async Task RunAsync(IServiceScopeFactory scopes, AiJob job, CancellationToken ct)
    {
        using var activity = SystemJobRunner.Source.StartActivity("AiGenerationJob");
        activity?.SetTag("reachforge.ai_job", job.Id);
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext
        {
            TenantId = job.TenantId,
            WorkspaceId = job.WorkspaceId,
            UserName = job.RequestedBy,
            Role = Role.Owner, // 依頼時に権限確認済み。ジョブはその代理で実行する
        };
        await scope.ServiceProvider.GetRequiredService<AiJobProcessor>().ProcessAsync(job.Id, ct);
    }
}
