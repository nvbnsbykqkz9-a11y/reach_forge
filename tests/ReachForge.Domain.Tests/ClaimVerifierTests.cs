using ReachForge.Domain.Analytics;

namespace ReachForge.Domain.Tests;

public class ClaimVerifierTests
{
    private static readonly Dictionary<string, ReportFact> Facts = new()
    {
        ["F1"] = new("F1", "表示回数", "12,340"),
        ["F2"] = new("F2", "反応の割合", "4.2%"),
        ["F3"] = new("F3", "反応の割合（前期間比）", "+12.5%"),
    };

    [Fact]
    public void Accepts_claims_whose_numbers_match_cited_facts()
    {
        var (ok, ng) = ClaimVerifier.Verify([new ReportClaim("表示回数は12,340回、反応の割合は4.2%（前期間比+12.5%）でした。", ["F1", "F2", "F3"])], Facts);
        Assert.Single(ok);
        Assert.Empty(ng);
    }

    [Fact]
    public void Rejects_numbers_not_in_cited_facts_and_missing_evidence()
    {
        var (ok, ng) = ClaimVerifier.Verify(
        [
            new ReportClaim("反応の割合は5.0%に上がりました。", ["F2"]),     // 値が違う
            new ReportClaim("表示回数は12,340回でした。", ["F2"]),           // 引用したファクトに含まれない
            new ReportClaim("投稿は好調でした。", []),                        // 根拠なし
            new ReportClaim("表示回数が伸びました。", ["F9"]),                // 存在しない ID
            new ReportClaim("次は3つの施策を試しましょう。", ["F1"]),          // 小さな個数は許可
        ], Facts);
        Assert.Single(ok);
        Assert.Equal(4, ng.Count);
    }
}
