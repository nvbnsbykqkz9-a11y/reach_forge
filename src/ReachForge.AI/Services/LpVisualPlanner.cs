using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// LP の内容と素材画像から、広告のビジュアル案（切り口・見出し・画像生成と動画生成への指示）をつくる（構造化出力）。
/// 見出しは NG 語・規制表現・LP にない価格を確認し、問題があれば見出しを空にする（画像は見出しなしでつくる）。
/// </summary>
public sealed class LpVisualPlanner(IModelRouter router, IPromptCatalog prompts) : ILpVisualPlanner
{
    public const int MaxHeadline = 15;

    public async Task<IReadOnlyList<LpVisualConcept>> PlanAsync(BrandContext brand, WebPage page, IReadOnlyList<string> sourceDescriptions,
        CancellationToken ct)
    {
        if (sourceDescriptions.Count == 0) return [];
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new LpVisualsStubPayload(page, sourceDescriptions, brand.Profile.BrandName)).Apply();
        var system = (await prompts.RenderAsync(PromptKeys.LpVisuals, PromptLibrary.LpVisualValues(brand, sourceDescriptions.Count), ct)).Text;
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpVisualsDraft>(client,
            [new(ChatRole.System, system), new(ChatRole.User, PromptLibrary.LpVisualUser(page, sourceDescriptions))], options, ct);

        var guard = brand.ToGuardrailContext();
        var prices = LandingPageVideoPlanner.Prices($"{page.Title}\n{page.Description}\n{page.Text}");
        var visuals = new List<LpVisualConcept>();
        foreach (var v in draft.Visuals ?? [])
        {
            if (v.SourceIndex < 0 || v.SourceIndex >= sourceDescriptions.Count || visuals.Any(x => x.SourceIndex == v.SourceIndex)) continue;
            var headline = PostText.Truncate((v.Headline ?? "").Trim().TrimEnd('。'), MaxHeadline);
            if (!IsSafe(headline, guard, prices)) headline = "";
            visuals.Add(new LpVisualConcept(v.SourceIndex,
                PostText.Truncate((v.Angle ?? "").Trim(), 12),
                headline,
                Clean(v.ImagePrompt, "A clean, appealing advertising photo of the product with soft natural light."),
                Clean(v.MotionPrompt, "Slow push-in camera move.")));
        }
        // 案が足りない素材は、ひかえめな指示で補う（画像は必ず素材ごとにつくる）
        for (var i = 0; i < sourceDescriptions.Count; i++)
        {
            if (visuals.Any(v => v.SourceIndex == i)) continue;
            visuals.Add(new LpVisualConcept(i, "", "", "A clean, appealing advertising photo of the product with soft natural light.",
                "Slow push-in camera move."));
        }
        return [.. visuals.OrderBy(v => v.SourceIndex)];
    }

    private static bool IsSafe(string text, GuardrailContext guard, HashSet<string> prices) =>
        text.Length == 0
        || (!GuardrailChecker.CheckContent(text, guard).Findings.Any(f => f.Level == GuardrailLevel.Error
                                                                         || f.Code is GuardrailCodes.RegulatedExpression or GuardrailCodes.PharmaExpression)
            && LandingPageVideoPlanner.Prices(text).All(prices.Contains));

    /// <summary>画像・動画生成への指示（英語）。長すぎるものは切り、空なら既定の指示にする。</summary>
    private static string Clean(string? prompt, string fallback)
    {
        var value = (prompt ?? "").ReplaceLineEndings(" ").Trim();
        return value.Length == 0 ? fallback : value.Length > 600 ? value[..600] : value;
    }
}
