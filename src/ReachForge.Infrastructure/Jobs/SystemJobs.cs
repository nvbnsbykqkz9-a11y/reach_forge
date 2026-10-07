using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// 定期ジョブの定義（14章）。分析・レポート・A/B テスト・ネタ帳の機能は削除したため、それらのジョブ（指標の取得・レポート・A/B の判定・話題の調査）は登録しない。
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
        new("token-refresh", "TokenRefreshJob", "0 3 * * *", 3, async (sp, ct) =>
        {
            var (refreshed, reauth) = await sp.GetRequiredService<ChannelTokenService>().RefreshExpiringAsync(ct);
            return refreshed + reauth > 0 ? $"{refreshed} refreshed, {reauth} need reconnection" : null;
        }),
        // チャネルごとの間隔（5〜15分）は InboxService.PollInterval で判定する
        new("inbox-poll", "InboxPollJob", "* * * * *", 0, async (sp, ct) =>
        {
            var r = await sp.GetRequiredService<InboxService>().PollDueAsync(ct);
            return r.Ingested + r.Failed > 0
                ? $"{r.Ingested} new from {r.Channels} channel(s), {r.AutoHandled} handled automatically, {r.Failed} failed"
                : null;
        }),
        new("credit-reset", "CreditResetJob", "0 * * * *", 0, async (sp, ct) =>
        {
            var n = await sp.GetRequiredService<CreditResetService>().ResetDueAsync(ct);
            return n > 0 ? $"{n} account(s) reset" : null;
        }),
        new("data-retention", "DataRetentionJob", "0 2 * * *", 0, async (sp, ct) =>
        {
            var r = await sp.GetRequiredService<DataRetentionService>().PurgeAsync(ct);
            return r.Total > 0
                ? $"deleted {r.Idempotency} idempotency, {r.AuditLogs} audit, {r.AiJobs} AI job, {r.TrendIdeas} idea, {r.Invitations} invitation row(s); "
                  + $"rolled up {r.PostMetricsRolledUp} post metric(s); partitions +{r.PartitionsCreated}/-{r.PartitionsDropped}"
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
