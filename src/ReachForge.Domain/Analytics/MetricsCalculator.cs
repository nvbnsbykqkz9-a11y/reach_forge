using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Analytics;

/// <summary>SNS 横断の正規化指標（RF-DES-001 F-10）。数値計算はここで行い、LLM には計算させない。</summary>
public static class MetricsCalculator
{
    /// <summary>
    /// エンゲージメント率＝（いいね＋コメント＋シェア＋保存）÷ 表示回数。
    /// 表示回数を提供しない SNS はリーチ→再生数の順で代替する。分母が0なら null。
    /// </summary>
    public static double? EngagementRate(PostMetric m)
    {
        var denominator = m.Impressions > 0 ? m.Impressions : m.Reach > 0 ? m.Reach : m.Views;
        if (denominator <= 0) return null;
        return (double)(m.Likes + m.Comments + m.Shares + m.Saves) / denominator;
    }

    public static long Engagements(PostMetric m) => m.Likes + m.Comments + m.Shares + m.Saves;

    /// <summary>送客率＝リンククリック ÷ 表示回数。</summary>
    public static double? ClickThroughRate(PostMetric m) =>
        m.Impressions > 0 ? (double)m.LinkClicks / m.Impressions : null;

    /// <summary>前期間比（%）。前期間が0なら null。</summary>
    public static double? ChangeRatio(double current, double previous) =>
        previous == 0 ? null : (current - previous) / previous;
}
