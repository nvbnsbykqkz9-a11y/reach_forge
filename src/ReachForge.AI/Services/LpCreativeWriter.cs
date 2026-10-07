using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>LP から、SNS ごとの広告文（3案）と投稿文・ハッシュタグをつくる。文字数の上限に収め、ガードレールの結果を添える。</summary>
public sealed class LpCreativeWriter(IModelRouter router, IPromptCatalog prompts) : ILpCreativeWriter
{
    private static readonly HashSet<string> CallsToAction = ["LEARN_MORE", "SHOP_NOW", "SIGN_UP", "CONTACT_US", "BOOK_NOW"];

    public async Task<LpCreative> WriteAsync(BrandContext brand, WebPage page, SocialPlatform platform, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new LpCreativeStubPayload(page, platform, brand.Profile.BrandName)).Apply();
        var system = await prompts.RenderAsync(PromptKeys.LpCreative, PromptLibrary.LpCreativeValues(brand, platform), ct);
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpCreativeDraft>(client,
            [new(ChatRole.System, system.Text), new(ChatRole.User, PromptLibrary.LandingPageUser(page))], options, ct);

        var c = PlatformCatalog.Get(platform);
        var limits = AdCopyLimits.For(platform);
        var copies = (draft.AdCopies ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.PrimaryText))
            .Take(3)
            .Select(x => new LpAdCopy(
                PostText.Truncate(PostText.RemoveUrls(x.PrimaryText).Trim(), limits.PrimaryText),
                limits.Headline == 0 ? "" : PostText.Truncate((x.Headline ?? "").Trim(), limits.Headline),
                limits.Description == 0 ? "" : PostText.Truncate((x.Description ?? "").Trim(), limits.Description),
                CallsToAction.Contains(x.CallToAction ?? "") ? x.CallToAction! : "LEARN_MORE"))
            .ToList();
        if (copies.Count == 0) throw new AiUnavailableException("AIから広告文の案を受け取れませんでした。もう一度お試しください。");

        var hashtags = (draft.Hashtags ?? []).Select(PostText.Normalize).Where(h => h.Length > 0).Distinct().Take(c.MaxHashtags ?? 30).ToList();
        var post = PostText.RemoveUrls(draft.PostText ?? "").Trim();
        var maxBody = c.MaxBodyLength - PostText.Length(PostText.Compose("", hashtags));
        post = PostText.Truncate(post, Math.Max(1, maxBody));

        var guardrail = brand.ToGuardrailContext();
        var text = string.Join("\n", copies.SelectMany(x => new[] { x.Headline, x.PrimaryText, x.Description }).Append(post));
        return new LpCreative(copies, post, hashtags, GuardrailChecker.CheckContent(text, guardrail));
    }
}
