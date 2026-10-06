namespace ReachForge.Domain.Analytics;

/// <summary>投稿指標の取得タイミング（公開後 1h, 6h, 24h, 72h, 7d, 30d：RF-DES-001 6.2 post_metric）。</summary>
public static class MetricSchedule
{
    public static readonly TimeSpan[] Checkpoints =
    [
        TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24),
        TimeSpan.FromHours(72), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
    ];

    /// <summary>最後の取得から収集を続ける期間（30日の取得が遅れた場合の猶予を含む）。</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(31);

    /// <summary>
    /// いま取得すべきか。直近に到達したチェックポイントの時刻より後にまだ取得していなければ true。
    /// 取得が遅れて複数のチェックポイントを過ぎていても、1回の取得でまとめて満たす。
    /// </summary>
    public static bool IsDue(DateTimeOffset publishedAt, DateTimeOffset? lastCapturedAt, DateTimeOffset now)
    {
        if (now - publishedAt > Horizon) return false;
        DateTimeOffset? reached = null;
        foreach (var c in Checkpoints)
        {
            if (publishedAt + c <= now) reached = publishedAt + c;
        }
        return reached is { } r && (lastCapturedAt is null || lastCapturedAt < r);
    }
}
