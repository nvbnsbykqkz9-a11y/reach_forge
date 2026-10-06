using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Hosting;

/// <summary>
/// AI ジョブ（画像生成・編集）の実行ループ（14章 AiGenerationJob）。初期実装は DB のキューをポーリングする。
/// 各ジョブは依頼したテナント・ワークスペースのコンテキストで実行する。大量処理時は Service Bus へ移行する。
/// </summary>
public sealed class AiJobDispatcher(IServiceScopeFactory scopes, ILogger<AiJobDispatcher> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public const int BatchSize = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
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

        foreach (var job in queued)
        {
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
        return queued.Count;
    }
}
