using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Engagement;

public sealed record FlameSignal(string Reason, int NegativeCount, double RecentRatio, double BaselineRatio, Guid? PostVariantId);

/// <summary>
/// 炎上の兆しの検知（F-09 処理 4）：
/// ・直近1時間の否定的な反応の割合が、平常時（直近7日間）の3倍を超える（直近の件数が少ないときは判定しない）
/// ・特定の投稿へのコメントで、直近1時間に否定的なものが急増している
/// </summary>
public static class FlameDetector
{
    public const int MinRecentMessages = 5;
    public const int PostSpikeThreshold = 5;
    public const double RatioMultiplier = 3.0;

    /// <summary>平常時の割合がほぼ 0 の場合の下限（少数のクレームで過敏に反応しないため）。</summary>
    public const double BaselineFloor = 0.05;

    public static FlameSignal? Detect(IReadOnlyCollection<InboxMessage> lastWeek, DateTimeOffset now)
    {
        var recent = lastWeek.Where(m => m.ReceivedAt > now.AddHours(-1) && m.Intent != InboxIntent.Spam).ToList();
        var baseline = lastWeek.Where(m => m.ReceivedAt <= now.AddHours(-1) && m.Intent != InboxIntent.Spam).ToList();
        var recentNegative = recent.Count(m => m.Sentiment == Sentiment.Negative);
        var baselineRatio = Math.Max(BaselineFloor,
            baseline.Count == 0 ? 0 : (double)baseline.Count(m => m.Sentiment == Sentiment.Negative) / baseline.Count);

        var spike = recent.Where(m => m.Sentiment == Sentiment.Negative && m.PostVariantId is not null)
            .GroupBy(m => m.PostVariantId)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (spike is not null && spike.Count() >= PostSpikeThreshold)
        {
            return new FlameSignal($"1つの投稿に否定的なコメントが1時間で{spike.Count()}件届いています", spike.Count(),
                recent.Count == 0 ? 0 : (double)recentNegative / recent.Count, baselineRatio, spike.Key);
        }

        if (recent.Count < MinRecentMessages) return null;
        var ratio = (double)recentNegative / recent.Count;
        return ratio > baselineRatio * RatioMultiplier
            ? new FlameSignal($"直近1時間の否定的な反応が{ratio:P0}（ふだんは{baselineRatio:P0}）に増えています", recentNegative, ratio, baselineRatio, null)
            : null;
    }
}
