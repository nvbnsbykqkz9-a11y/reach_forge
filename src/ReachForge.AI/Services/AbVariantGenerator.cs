using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.AI.Services;

/// <summary>B 案の生成（書き出し・CTA だけを変える）。画像・投稿時間のテストは本文を変えない。</summary>
public sealed class AbVariantGenerator(IModelRouter router) : IAbVariantGenerator
{
    public async Task<string> GenerateAsync(string body, AbVariable variable, BrandContext brand, CancellationToken ct)
    {
        if (variable is AbVariable.Image or AbVariable.TimeSlot) return body;
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new AbStubPayload(body, variable)).Apply();
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<AbVariantDraft>(client,
            [new(ChatRole.System, PromptLibrary.AbSystem(brand, variable)), new(ChatRole.User, PromptInjectionDetector.Fence(body))],
            options, ct);
        return string.IsNullOrWhiteSpace(draft.Body) ? body : draft.Body.Trim();
    }
}
