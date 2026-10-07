using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Services;

/// <summary>ネタの採点と切り口（Research Agent）。</summary>
public sealed class TrendIdeaWriter(IModelRouter router, IPromptCatalog prompts) : ITrendIdeaWriter
{
    public async Task<IReadOnlyList<ScoredIdea>> ScoreAsync(BrandContext brand, IReadOnlyList<TrendCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return [];
        var client = router.Resolve(AiTaskType.Ideation);
        var options = new AiCallContext(AiTaskType.Ideation, null, new TrendStubPayload(brand, candidates)).Apply();
        var (batch, _) = await CopyGenerationService.GetStructuredAsync<IdeaBatch>(client,
            [new(ChatRole.System, (await prompts.RenderAsync(PromptKeys.TrendIdeas, PromptLibrary.BrandValues(brand), ct)).Text), new(ChatRole.User, PromptLibrary.TrendUser(candidates))], options, ct);
        var known = candidates.Select(c => c.Topic).ToHashSet();
        return (batch.Ideas ?? [])
            .Where(i => i.Topic is not null && known.Contains(i.Topic)) // 候補にない話題は採用しない
            .Select(i => new ScoredIdea(i.Topic, Math.Clamp(i.Relevance, 0, 1), i.Format ?? "画像1枚",
                (i.Angles ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Take(3).ToList(), i.Reason ?? "", i.Sensitive, Math.Clamp(i.DaysBefore, 0, 14)))
            .ToList();
    }
}
