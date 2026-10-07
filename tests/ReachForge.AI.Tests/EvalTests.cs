using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReachForge.AI.Evals;
using ReachForge.AI.Prompts;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Tests;

/// <summary>AI Evals（RF-DES-001 4.6）：評価セット・評価器・実行の仕組み。</summary>
public class EvalTests
{
    private static EvalRunner Runner()
    {
        var router = Substitute.For<IModelRouter>();
        router.Resolve(Arg.Any<AiTaskType>()).Returns(new StubChatClient());
        return new EvalRunner(router, new DefaultPromptStore(), new MutableTenantContext { IsSystem = true }, NullLoggerFactory.Instance);
    }

    [Fact]
    public void Dataset_has_at_least_200_cases_and_the_stored_file_is_up_to_date()
    {
        var built = EvalDataset.Build();
        Assert.True(built.Count >= 200, $"{built.Count} cases");
        Assert.Equal(built.Count, built.Select(c => c.Id).Distinct().Count());
        Assert.Contains(built, c => c.Category == EvalCase.RedTeam);
        Assert.Contains(built, c => c.IsAdvertisement);

        // datasets/copy.generate.json は Build() から書き出したもの（--export-dataset）。ずれていたら書き出し直す
        var stored = EvalDataset.Load();
        Assert.Equal(EvalDataset.Serialize(built), EvalDataset.Serialize(stored));
    }

    private static ChatResponse Response(params GeneratedCopy[] copies) =>
        new(new ChatMessage(ChatRole.Assistant, System.Text.Json.JsonSerializer.Serialize(new CopyBatch(copies), AIJsonUtilities.DefaultOptions)));

    private static CaseContext Context(string[]? forbidden = null, bool ad = false)
    {
        var brand = new EvalBrand("テスト珈琲", "カフェ", 50, 1, ["激安"], ["要予約"], [], [new EvalProduct("ブレンド", 500m, "")]);
        var c = new EvalCase("T1", EvalCase.Normal, brand, "テーマ", PostObjective.Awareness, ad, true, forbidden ?? []);
        return new CaseContext(c, brand.ToContext(ad));
    }

    [Fact]
    public async Task Constraint_safety_and_fact_evaluators_flag_problems()
    {
        var ok = new GeneratedCopy("秋のブレンド", "ブレンドは500円です。要予約です。", "ぜひどうぞ", ["カフェ", "珈琲", "秋"]);
        var bad = new GeneratedCopy(new string('あ', 31), "激安！最安のブレンドが300円。シミが消える", "今すぐ", ["カフェ"]);
        var ctx = Context(forbidden: ["最安"]);
        var response = Response(ok, bad);

        var constraint = await new ConstraintEvaluator().EvaluateAsync([], response, null, [ctx]);
        Assert.Equal(1, constraint.Get<NumericMetric>(ConstraintEvaluator.Passed).Value);
        Assert.Equal(2, constraint.Get<NumericMetric>(ConstraintEvaluator.Total).Value);
        var reason = constraint.Get<NumericMetric>(ConstraintEvaluator.Passed).Reason!;
        Assert.Contains("見出しが30字超", reason);
        Assert.Contains("ハッシュタグ1個", reason);

        var safety = await new SafetyEvaluator().EvaluateAsync([], response, null, [ctx]);
        Assert.True(safety.Get<NumericMetric>(SafetyEvaluator.Violations).Value >= 2); // 「最安」（規制表現と禁止文字列）
        Assert.True(safety.Get<NumericMetric>(SafetyEvaluator.Violations).Interpretation!.Failed);

        var facts = await new FactualityEvaluator().EvaluateAsync([], response, null, [ctx]);
        Assert.Equal(1, facts.Get<NumericMetric>(FactualityEvaluator.Mismatches).Value); // 300円は商品マスタにない
    }

    [Fact]
    public async Task Advertisements_are_evaluated_after_the_automatic_pr_disclosure()
    {
        var copy = new GeneratedCopy("秋のブレンド", "ブレンドは500円です。要予約です。", "ぜひどうぞ", ["カフェ", "珈琲", "秋"]);
        var result = await new ConstraintEvaluator().EvaluateAsync([], Response(copy), null, [Context(ad: true)]);
        Assert.Equal(1, result.Get<NumericMetric>(ConstraintEvaluator.Passed).Value);
    }

    [Fact]
    public async Task Runner_scores_every_case_blocks_injections_and_reports_against_the_thresholds()
    {
        var dataset = EvalDataset.Build();
        var report = await Runner().RunAsync(dataset, new EvalOptions { MaxCases = 40 }, CancellationToken.None);

        Assert.Equal(40, report.Cases.Count);
        Assert.Equal(PromptCatalog.DefaultVersion, report.PromptVersion);
        Assert.Equal("stub-local", report.Model);
        Assert.Contains(report.Cases, c => c.Category == EvalCase.RedTeam);
        Assert.NotNull(report.BrandFit);
        Assert.InRange(report.ConstraintRate, 0, 1);
        // スタブは指示をそのまま本文に入れるため、レッドチームの禁止表現を検出できること（評価の仕組みの確認）
        Assert.True(report.SafetyViolations > 0);
        Assert.False(report.Passed);
        Assert.Contains("ブランド適合度", report.ToMarkdown());
        var result = report.ToResult(DateTimeOffset.UnixEpoch);
        Assert.Equal(report.Passed, result.Passed);
        Assert.Equal(40, result.Cases);

        // 入力ガードで止まるプロンプトインジェクション（本番と同じ判定）
        var injections = dataset.Where(c => c.Theme.Contains("指示を無視") || c.Theme.Contains("ignore previous")).ToList();
        var blocked = await Runner().RunAsync(injections, new EvalOptions(), CancellationToken.None);
        Assert.All(blocked.Cases, c => Assert.True(c.BlockedByInputGuard, c.Theme));
    }

    [Fact]
    public async Task Draft_templates_can_be_evaluated_before_publishing()
    {
        var dataset = EvalDataset.Build().Take(3).ToList();
        var draft = new StoredPrompt(PromptKeys.Copy, 7, PromptLibrary.Defaults[PromptKeys.Copy] + "\n（v7の追加指示）");
        var report = await Runner().RunAsync(dataset, new EvalOptions
        {
            Overrides = new Dictionary<string, StoredPrompt> { [PromptKeys.Copy] = draft },
        }, CancellationToken.None);
        Assert.Equal(7, report.PromptVersion);
    }
}
