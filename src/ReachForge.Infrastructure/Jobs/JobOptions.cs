namespace ReachForge.Infrastructure.Jobs;

public enum JobEngine
{
    /// <summary>プロセス内のタイマーで定期ジョブを動かす（開発・単一インスタンス向け。追加の基盤は不要）。</summary>
    Hosted,

    /// <summary>Hangfire（PostgreSQL ストレージ）で動かす。複数インスタンスでも1回だけ実行し、履歴と再試行をダッシュボードで確認できる。</summary>
    Hangfire,
}

public enum QueueProvider
{
    /// <summary>プロセス内のキュー。再起動で消えるため、取りこぼしは巡回（InboxPollJob・AiJobDispatcher）で補う。</summary>
    InProcess,

    /// <summary>Azure Service Bus（再試行5回 → デッドレター）。</summary>
    ServiceBus,
}

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    public JobEngine Engine { get; set; } = JobEngine.Hosted;

    /// <summary>定期ジョブの時刻（cron）を解釈するタイムゾーン（14章は JST 基準）。</summary>
    public string TimeZone { get; set; } = "Asia/Tokyo";

    public QueueProvider Queue { get; set; } = QueueProvider.InProcess;

    public ServiceBusQueueOptions ServiceBus { get; set; } = new();

    public HangfireJobOptions Hangfire { get; set; } = new();

    /// <summary>
    /// 待機中の AI ジョブを DB から拾う間隔（秒）。キューの通知が届かなかった場合の保険。
    /// 未指定ならプロセス内キューは 2 秒、Service Bus は 30 秒。
    /// </summary>
    public int? AiSweepSeconds { get; set; }

    public TimeSpan AiSweepInterval => TimeSpan.FromSeconds(AiSweepSeconds ?? (Queue == QueueProvider.ServiceBus ? 30 : 2));
}

public sealed class ServiceBusQueueOptions
{
    /// <summary>接続文字列（ローカル・検証用）。本番は <see cref="FullyQualifiedNamespace"/> とマネージド ID を使う。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>例：reachforge.servicebus.windows.net</summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>起動時にキューがなければ作る（MaxDeliveryCount=5、デッドレター有効）。管理権限が必要。</summary>
    public bool CreateQueues { get; set; }

    public int MaxConcurrentCalls { get; set; } = 4;
}

public sealed class HangfireJobOptions
{
    /// <summary>Hangfire のテーブルを置くスキーマ（PostgreSQL）。</summary>
    public string Schema { get; set; } = "hangfire";

    /// <summary>起動時に Hangfire のテーブルを作成・更新する。</summary>
    public bool PrepareSchema { get; set; } = true;

    public int WorkerCount { get; set; } = 5;
}
