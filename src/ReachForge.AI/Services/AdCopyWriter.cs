using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>有料広告の広告文（3案）。各社の文字数の上限に収め、ガードレール（NG ワード・規制表現）の結果を添える。</summary>
public sealed class AdCopyWriter(IModelRouter router, IPromptCatalog prompts) : IAdCopyWriter
{
    private static readonly HashSet<string> CallsToAction = ["LEARN_MORE", "SHOP_NOW", "SIGN_UP", "CONTACT_US", "BOOK_NOW"];

    public async Task<IReadOnlyList<AdCopyCandidate>> WriteAsync(BrandContext brand, AdCopyRequest request, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new AdCopyStubPayload(request, brand.Profile.BrandName)).Apply();
        var system = await prompts.RenderAsync(PromptKeys.AdCopy, PromptLibrary.AdCopyValues(brand, request), ct);
        var (batch, _) = await CopyGenerationService.GetStructuredAsync<AdCopyBatch>(client,
            [new(ChatRole.System, system.Text), new(ChatRole.User, PromptLibrary.AdCopyUser(request))], options, ct);

        var limits = AdCopyLimits.For(request.Platform);
        var guardrail = brand.ToGuardrailContext();
        var result = (batch.Candidates ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.PrimaryText))
            .Take(3)
            .Select(c =>
            {
                var primary = PostText.Truncate(c.PrimaryText.Trim(), limits.PrimaryText);
                var headline = limits.Headline == 0 ? "" : PostText.Truncate((c.Headline ?? "").Trim(), limits.Headline);
                var description = limits.Description == 0 ? "" : PostText.Truncate((c.Description ?? "").Trim(), limits.Description);
                var cta = CallsToAction.Contains(c.CallToAction ?? "") ? c.CallToAction! : "LEARN_MORE";
                return new AdCopyCandidate(primary, headline, description, cta,
                    GuardrailChecker.CheckContent($"{headline}\n{primary}\n{description}", guardrail));
            })
            .ToList();
        return result.Count > 0 ? result : throw new AiUnavailableException("AIから広告文の案を受け取れませんでした。もう一度お試しください。");
    }
}
