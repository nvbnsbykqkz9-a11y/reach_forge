namespace ReachForge.Domain.Analytics;

public enum AbVerdict { Pending, AWins, BWins, NoDifference }

public sealed record AbTestResult(AbVerdict Verdict, double RateA, double RateB, double? PValue, string Summary);

/// <summary>
/// A/B テストの判定（F-11-3）。72時間後のエンゲージメント率を二項比率の検定（有意水準5%）で比較する。
/// 結果は統計用語を使わず結論から表示する（RF-UX-001 SCR-11）。
/// </summary>
public static class AbTestEvaluator
{
    public const double Alpha = 0.05;
    public const long MinimumImpressions = 1000;

    public static AbTestResult Evaluate(long impressionsA, long engagementsA, long impressionsB, long engagementsB)
    {
        var rateA = impressionsA > 0 ? (double)engagementsA / impressionsA : 0;
        var rateB = impressionsB > 0 ? (double)engagementsB / impressionsB : 0;

        if (impressionsA < MinimumImpressions || impressionsB < MinimumImpressions)
        {
            return new(AbVerdict.Pending, rateA, rateB, null, "まだ判断できません（表示回数が足りません）");
        }

        var pooled = (double)(engagementsA + engagementsB) / (impressionsA + impressionsB);
        var se = Math.Sqrt(pooled * (1 - pooled) * (1.0 / impressionsA + 1.0 / impressionsB));
        if (se == 0)
        {
            return new(AbVerdict.NoDifference, rateA, rateB, 1, "AとBの反応に差はありません");
        }

        var z = (rateB - rateA) / se;
        var p = 2 * (1 - NormalCdf(Math.Abs(z)));
        if (p >= Alpha)
        {
            return new(AbVerdict.NoDifference, rateA, rateB, p, "AとBの反応にはっきりした差はありません");
        }

        var (winner, loserRate, winnerRate) = z > 0 ? ("B", rateA, rateB) : ("A", rateB, rateA);
        var ratio = loserRate > 0 ? winnerRate / loserRate : double.PositiveInfinity;
        return new(z > 0 ? AbVerdict.BWins : AbVerdict.AWins, rateA, rateB, p,
            $"{winner}の方が反応が {ratio:0.0}倍（信頼できる差）");
    }

    /// <summary>標準正規分布の累積分布関数（Abramowitz–Stegun 7.1.26 による erf 近似）。</summary>
    internal static double NormalCdf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2));
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t
            * Math.Exp(-x * x / 2);
        return 0.5 * (1 + Math.Sign(x) * erf);
    }
}
