using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Analytics;

/// <summary>おすすめ投稿時刻の1枠。</summary>
public sealed record BestTimeSlot(DayOfWeek Day, int Hour, double Score, int SampleSize, string Rationale);

/// <summary>
/// 最適投稿時刻の提案（F-08-1）。チャネル別に過去90日の「投稿時刻×エンゲージメント率」を曜日・時間帯で集計し、上位3枠を返す。
/// 実績が30件未満の場合は業種別の一般傾向（既定値）を返す。
/// </summary>
public static class BestTimeCalculator
{
    public const int LookbackDays = 90;
    public const int MinimumSamples = 30;

    /// <summary>業種別の一般傾向が未設定のときの既定枠（平日昼・夜、週末朝）。</summary>
    public static readonly IReadOnlyList<(DayOfWeek Day, int Hour)> DefaultSlots =
    [
        (DayOfWeek.Thursday, 19),
        (DayOfWeek.Wednesday, 12),
        (DayOfWeek.Saturday, 9),
    ];

    /// <param name="metrics">各投稿の最新指標（1投稿1件）。</param>
    /// <param name="timeZone">集計に使うタイムゾーン（テナント TZ）。</param>
    public static IReadOnlyList<BestTimeSlot> Suggest(IEnumerable<PostMetric> metrics, TimeZoneInfo timeZone,
        DateTimeOffset now, int take = 3)
    {
        var since = now.AddDays(-LookbackDays);
        var samples = metrics
            .Where(m => m.PostedAt >= since)
            .Select(m => (Local: TimeZoneInfo.ConvertTime(m.PostedAt, timeZone), Rate: MetricsCalculator.EngagementRate(m)))
            .Where(x => x.Rate is not null)
            .ToList();

        if (samples.Count < MinimumSamples)
        {
            return DefaultSlots.Take(take)
                .Select(s => new BestTimeSlot(s.Day, s.Hour, 0, samples.Count,
                    $"実績が{MinimumSamples}件未満のため、一般的に反応が多い時間帯を表示しています"))
                .ToList();
        }

        var overall = samples.Average(x => x.Rate!.Value);
        return samples
            .GroupBy(x => (x.Local.DayOfWeek, x.Local.Hour))
            .Select(g => (g.Key, Avg: g.Average(x => x.Rate!.Value), Count: g.Count()))
            // 件数が極端に少ない枠の偶然の高スコアを抑える（全体平均へ縮小）
            .Select(g => (g.Key, Score: (g.Avg * g.Count + overall * 3) / (g.Count + 3), g.Count))
            .OrderByDescending(g => g.Score)
            .Take(take)
            .Select(g => new BestTimeSlot(g.Key.DayOfWeek, g.Key.Hour, g.Score, g.Count,
                $"過去{LookbackDays}日で反応が平均の{(overall > 0 ? g.Score / overall : 1):0.0}倍の時間帯（{g.Count}件）"))
            .ToList();
    }

    /// <summary>指定の曜日・時刻の次回の日時（テナント TZ）を UTC で返す。</summary>
    public static DateTimeOffset NextOccurrence(BestTimeSlot slot, TimeZoneInfo timeZone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        var days = ((int)slot.Day - (int)local.DayOfWeek + 7) % 7;
        var candidate = new DateTime(local.Year, local.Month, local.Day, slot.Hour, 0, 0).AddDays(days);
        if (days == 0 && candidate <= local.DateTime) candidate = candidate.AddDays(7);
        return new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate)).ToUniversalTime();
    }
}
