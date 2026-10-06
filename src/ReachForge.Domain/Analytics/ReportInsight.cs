using System.Globalization;
using System.Text.RegularExpressions;

namespace ReachForge.Domain.Analytics;

/// <summary>AI に渡す根拠の数値（システムが計算した値）。主張はこの ID を引用する。</summary>
public sealed record ReportFact(string Id, string Label, string Value);

/// <summary>AI の主張1件と、その根拠ファクトの ID。</summary>
public sealed record ReportClaim(string Text, IReadOnlyList<string> FactIds);

/// <summary>AI レポートの考察（3行まとめ／良かった点／課題／次の施策3つ）。</summary>
public sealed record ReportInsight(
    IReadOnlyList<ReportClaim> Summary,
    IReadOnlyList<ReportClaim> Good,
    IReadOnlyList<ReportClaim> Issues,
    IReadOnlyList<ReportClaim> NextActions)
{
    public static readonly ReportInsight Empty = new([], [], [], []);
    public int Count => Summary.Count + Good.Count + Issues.Count + NextActions.Count;
}

/// <summary>
/// AI の主張の事後検証（F-10 AIレポート処理 3）。
/// ・根拠のファクト ID が1つ以上あり、すべて存在すること
/// ・文中の数値（%・回・件など）が、引用したファクトの値のいずれかと一致すること（小さな序数・個数 1〜10 は除く）
/// 満たさない主張は除外し、LLM に計算させた値が紛れ込まないようにする。
/// </summary>
public static partial class ClaimVerifier
{
    [GeneratedRegex(@"[+\-−]?\d[\d,]*(?:\.\d+)?\s*[%％]?")]
    private static partial Regex NumberPattern();

    public static (IReadOnlyList<ReportClaim> Accepted, IReadOnlyList<string> Rejected) Verify(
        IEnumerable<ReportClaim> claims, IReadOnlyDictionary<string, ReportFact> facts)
    {
        var accepted = new List<ReportClaim>();
        var rejected = new List<string>();
        foreach (var claim in claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Text)) continue;
            if (claim.FactIds.Count == 0)
            {
                rejected.Add($"根拠がない：{claim.Text}");
                continue;
            }
            var cited = claim.FactIds.Select(id => facts.GetValueOrDefault(id)).ToList();
            if (cited.Any(f => f is null))
            {
                rejected.Add($"存在しない根拠を引用：{claim.Text}");
                continue;
            }
            var allowed = cited.SelectMany(f => Numbers(f!.Value).Concat(Numbers(f.Label))).ToList();
            var unsupported = Numbers(claim.Text).Where(n => !IsTrivial(n) && !allowed.Any(a => Matches(n, a))).ToList();
            if (unsupported.Count > 0)
            {
                rejected.Add($"根拠と一致しない数値（{string.Join("、", unsupported.Select(u => u.Raw))}）：{claim.Text}");
                continue;
            }
            accepted.Add(claim);
        }
        return (accepted, rejected);
    }

    public static ReportInsight Verify(ReportInsight insight, IReadOnlyDictionary<string, ReportFact> facts, out IReadOnlyList<string> rejected)
    {
        var all = new List<string>();
        IReadOnlyList<ReportClaim> Check(IEnumerable<ReportClaim> claims)
        {
            var (ok, ng) = Verify(claims, facts);
            all.AddRange(ng);
            return ok;
        }
        var result = new ReportInsight(Check(insight.Summary), Check(insight.Good), Check(insight.Issues), Check(insight.NextActions));
        rejected = all;
        return result;
    }

    internal readonly record struct Number(string Raw, double Value, bool Percent);

    internal static IEnumerable<Number> Numbers(string text)
    {
        foreach (Match m in NumberPattern().Matches(text))
        {
            var raw = m.Value.Trim();
            var percent = raw.EndsWith('%') || raw.EndsWith('％');
            var digits = raw.TrimEnd('%', '％').Trim().Replace(",", "").Replace('−', '-').TrimStart('+');
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                yield return new Number(raw, Math.Abs(v), percent);
            }
        }
    }

    /// <summary>「3つ」「1位」などの小さな整数は許可する。</summary>
    private static bool IsTrivial(Number n) => !n.Percent && n.Value is >= 0 and <= 10 && n.Value == Math.Floor(n.Value);

    /// <summary>表記ゆれ（丸め・千区切り）を許容して比較する。</summary>
    private static bool Matches(Number claim, Number fact)
    {
        if (claim.Percent != fact.Percent) return false;
        var tolerance = claim.Percent ? 0.1 : Math.Max(0.5, fact.Value * 0.005);
        return Math.Abs(claim.Value - fact.Value) <= tolerance;
    }
}
