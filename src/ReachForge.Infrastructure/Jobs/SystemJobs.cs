using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// 定期ジョブの定義（14章）。SNS とはつながないため、クレジットの付与と古いデータの削除だけを行う。
/// <paramref name="Cron"/> は <see cref="JobOptions.TimeZone"/>（既定 JST）で解釈する。
/// 実行内容はどれも「期限が来たものだけを処理する」冪等な処理で、取りこぼしても次の周期で追いつく。
/// </summary>
/// <param name="Retries">失敗時の再試行回数（0 は次の周期まで待つ、または運用者が確認して再実行する）。</param>
/// <param name="Run">システムコンテキストのスコープで実行する。ログに出す要約を返す（何もしなければ null）。</param>
public sealed record SystemJob(string Id, string Name, string Cron, int Retries,
    Func<IServiceProvider, CancellationToken, Task<string?>> Run);

public static class SystemJobCatalog
{
    public static readonly IReadOnlyList<SystemJob> All =
    [
        new("credit-reset", "CreditResetJob", "0 * * * *", 0, async (sp, ct) =>
        {
            var n = await sp.GetRequiredService<CreditResetService>().ResetDueAsync(ct);
            return n > 0 ? $"{n} account(s) reset" : null;
        }),
        new("data-retention", "DataRetentionJob", "0 2 * * *", 0, async (sp, ct) =>
        {
            var r = await sp.GetRequiredService<DataRetentionService>().PurgeAsync(ct);
            return r.Total > 0
                ? $"deleted {r.AuditLogs} audit, {r.AiJobs} AI job, {r.Invitations} invitation row(s)"
                : null;
        }),
    ];

    public static SystemJob? Find(string id) => All.FirstOrDefault(j => j.Id == id);
}

/// <summary>定期ジョブを1回実行する（エンジンに依存しない部分）。例外はエンジンへ伝え、再試行に任せる。</summary>
public sealed class SystemJobRunner(IServiceScopeFactory scopes, ILogger<SystemJobRunner> log)
{
    public static readonly ActivitySource Source = new("ReachForge.Jobs");

    public async Task RunAsync(string jobId, CancellationToken ct)
    {
        var job = SystemJobCatalog.Find(jobId) ?? throw new ArgumentException($"Unknown job '{jobId}'", nameof(jobId));
        using var activity = Source.StartActivity(job.Name);
        activity?.SetTag("reachforge.job", job.Id);
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
            new MutableTenantContext { IsSystem = true, UserName = "system" };
        try
        {
            if (await job.Run(scope.ServiceProvider, ct) is { } summary)
            {
                log.LogInformation("{Job}: {Summary}", job.Name, summary);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
