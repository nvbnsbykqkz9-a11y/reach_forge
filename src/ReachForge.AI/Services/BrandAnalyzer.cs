using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>ブランド診断（F-02 処理 2）。色はページから抽出した値をそのまま使う（AI に推測させない）。</summary>
public sealed class BrandAnalyzer(IModelRouter router, IPromptCatalog prompts) : IBrandAnalyzer
{
    public async Task<BrandProfileDraft> AnalyzeAsync(BrandAnalysisInput input, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new BrandStubPayload(input)).Apply();
        var (o, _) = await CopyGenerationService.GetStructuredAsync<BrandDraftOutput>(client,
            [new(ChatRole.System, (await prompts.RenderAsync(PromptKeys.BrandDiagnosis, PromptLibrary.Values(), ct)).Text), new(ChatRole.User, PromptLibrary.BrandUser(input))], options, ct);
        return new BrandProfileDraft(
            o.BrandName?.Trim() ?? "",
            o.Industry?.Trim() ?? "",
            Math.Clamp(o.Casualness, 0, 100),
            string.IsNullOrWhiteSpace(o.FirstPerson) ? "私たち" : o.FirstPerson.Trim(),
            Math.Clamp(o.EmojiLevel, 0, 3),
            string.IsNullOrWhiteSpace(o.EndingRule) ? null : o.EndingRule.Trim(),
            (o.Personas ?? []).Take(3).ToList(),
            (o.AppealPoints ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Take(5).ToList(),
            (o.Hashtags ?? []).Select(PostText.Normalize).Where(x => x.Length > 0).Distinct().Take(8).ToList(),
            (o.NgWordSuggestions ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10).ToList(),
            input.Page?.Colors ?? [],
            (o.Faqs ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Question) && !string.IsNullOrWhiteSpace(f.Answer)).Take(10).ToList());
    }
}
