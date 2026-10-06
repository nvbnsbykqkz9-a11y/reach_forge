using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Services;

/// <summary>Analyst Agent（F-10 AIレポート処理 3）。構造化出力で主張と根拠 ID を受け取る。検証は呼び出し側で行う。</summary>
public sealed class ReportWriter(IModelRouter router) : IReportWriter
{
    public async Task<(ReportInsight Insight, AiModelInfo Model)> WriteAsync(ReportWriterInput input, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Report);
        var options = new AiCallContext(AiTaskType.Report, null, new ReportStubPayload(input)).Apply();
        var (draft, response) = await CopyGenerationService.GetStructuredAsync<InsightDraft>(client,
            [new(ChatRole.System, PromptLibrary.ReportSystem), new(ChatRole.User, PromptLibrary.ReportUser(input))], options, ct);

        static IReadOnlyList<ReportClaim> Map(ClaimDraft[]? claims) =>
            (claims ?? []).Select(c => new ReportClaim(c.Text?.Trim() ?? "", c.Evidence ?? [])).ToList();

        var insight = new ReportInsight(Map(draft.Summary).Take(3).ToList(), Map(draft.Good).Take(3).ToList(),
            Map(draft.Issues).Take(3).ToList(), Map(draft.NextActions).Take(3).ToList());
        return (insight, CopyGenerationService.ModelInfo(response));
    }
}
