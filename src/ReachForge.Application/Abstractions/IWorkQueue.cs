namespace ReachForge.Application.Abstractions;

/// <summary>キュー名（Service Bus のキュー名と同じ）。</summary>
public static class WorkQueues
{
    /// <summary>AiGenerationJob（14章：画像・動画の生成）。本文は AiJob の ID。</summary>
    public const string AiJobs = "ai-jobs";

    public static readonly IReadOnlyList<string> All = [AiJobs];

    /// <summary>この回数失敗したメッセージはデッドレターキューへ移す（14章）。</summary>
    public const int MaxDeliveryCount = 5;
}

/// <summary>
/// 非同期処理のキュー（14章）。既定はプロセス内（単一インスタンス・開発向け）、本番は Azure Service Bus。
/// </summary>
public interface IWorkQueue
{
    /// <summary>メッセージを登録する。登録できなかった場合は例外を投げる。</summary>
    Task EnqueueAsync(string queue, string payload, CancellationToken ct);
}

/// <summary>キューのメッセージを処理する。例外を投げると再試行され、上限に達するとデッドレターになる。</summary>
public interface IWorkHandler
{
    string Queue { get; }

    Task HandleAsync(string payload, CancellationToken ct);
}

public sealed record DeadLetter(string Queue, string MessageId, string Payload, string? Reason, DateTimeOffset EnqueuedAt);

/// <summary>デッドレターの確認と再投入（運用管理画面）。</summary>
public interface IDeadLetterAdmin
{
    Task<IReadOnlyList<DeadLetter>> PeekAsync(string queue, int max, CancellationToken ct);

    /// <summary>デッドレターを元のキューへ戻す。戻した件数を返す。</summary>
    Task<int> RequeueAsync(string queue, CancellationToken ct);
}

public static class WorkQueueExtensions
{
    /// <summary>
    /// AI ジョブの実行を依頼する。ジョブは DB に保存済みで、届かなくても AiJobDispatcher の巡回で実行されるため、
    /// 登録の失敗は呼び出し元へ伝えない。
    /// </summary>
    public static async Task NotifyAiJobAsync(this IWorkQueue queue, Guid jobId, CancellationToken ct)
    {
        try
        {
            await queue.EnqueueAsync(WorkQueues.AiJobs, jobId.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 巡回で拾う
        }
    }
}
