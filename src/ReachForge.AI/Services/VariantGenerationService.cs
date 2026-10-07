using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// マルチSNS最適化変換（RF-DES-001 F-06）。並列実行されるため DbContext には触れない（計量は IAiUsageSink 経由）。
/// ルールベース検証で不適合なら AI による自動修正を最大2回試み、それでも満たさない場合は機械的に整える。
/// </summary>
public sealed class VariantGenerationService(IModelRouter router, IPromptCatalog prompts) : IVariantGenerationService
{
    public const int MaxAutoFixAttempts = 2;

    public async Task<GeneratedVariant> GenerateAsync(VariantRequest request, Guid? generationId, CancellationToken ct)
    {
        var constraint = PlatformCatalog.Get(request.Platform);
        var link = ResolveLink(request, constraint);
        var client = router.Resolve(AiTaskType.Variant);
        var system = await prompts.RenderAsync(PromptKeys.Variant, PromptLibrary.VariantValues(constraint), ct);

        string? feedback = null;
        VariantDraft draft = new("", [], null);
        ChatResponse? response = null;
        var attempts = 0;
        for (; attempts <= MaxAutoFixAttempts; attempts++)
        {
            var options = new AiCallContext(AiTaskType.Variant, generationId,
                new VariantStubPayload(request, link, feedback)).Apply();
            (draft, response) = await CopyGenerationService.GetStructuredAsync<VariantDraft>(client,
                [
                    new(ChatRole.System, system.Text),
                    new(ChatRole.User, PromptLibrary.VariantUser(request, link, feedback)),
                ], options, ct);

            var problems = Problems(draft, constraint, link);
            if (problems.Count == 0) break;
            feedback = string.Join(" / ", problems);
        }

        var (body, tags, title) = Normalize(draft, constraint, link);
        var report = GuardrailChecker.CheckPlatform(PostText.Compose(body, tags), constraint, title);
        return new GeneratedVariant(body, tags, title, report, CopyGenerationService.ModelInfo(response!),
            Math.Min(attempts, MaxAutoFixAttempts));
    }

    /// <summary>リンクの扱い：UTM を付与し、SNS の方針に合わせて本文に入れるか決める（F-06-4）。</summary>
    internal static string? ResolveLink(VariantRequest r, PlatformConstraint c)
    {
        if (string.IsNullOrWhiteSpace(r.LinkUrl)) return null;
        var url = UtmBuilder.Append(r.LinkUrl.Trim(), r.Platform, r.CampaignCode);
        return c.LinkPolicy switch
        {
            LinkPolicy.Allowed or LinkPolicy.Required => url,
            LinkPolicy.DiscouragedByCost => r.IncludeUrlForX ? url : null,
            _ => null, // Instagram（本文リンク無効）・TikTok（不可）は入れない
        };
    }

    private static List<string> Problems(VariantDraft d, PlatformConstraint c, string? link)
    {
        var problems = new List<string>();
        var composed = PostText.Compose(d.Body, d.Hashtags);
        if (PostText.Length(composed) > c.MaxBodyLength) problems.Add($"本文とハッシュタグを合わせて{c.MaxBodyLength}字以内にする");
        var tagCount = PostText.Hashtags(composed).Count;
        if (c.MaxHashtags is { } max && tagCount > max) problems.Add($"ハッシュタグを{max}個以内にする");
        if (c.MaxTitleLength is { } mt && d.Title is not null && PostText.Length(d.Title) > mt) problems.Add($"タイトルを{mt}字以内にする");
        if (link is null && PostText.ContainsUrl(d.Body)) problems.Add("本文からURLを外す");
        if (link is not null && c.LinkPolicy == LinkPolicy.Required && !d.Body.Contains(link)) problems.Add("指定のURLを本文に入れる");
        return problems;
    }

    /// <summary>最終的な機械的補正（自動修正で直らなかった場合の安全策）。</summary>
    private static (string Body, IReadOnlyList<string> Tags, string? Title) Normalize(VariantDraft d, PlatformConstraint c,
        string? link)
    {
        var body = d.Body.Trim();
        if (link is null && PostText.ContainsUrl(body)) body = PostText.RemoveUrls(body);
        if (link is not null && c.LinkPolicy == LinkPolicy.Required && !body.Contains(link)) body = $"{body}\n{link}";

        var bodyTags = PostText.Hashtags(body).Count;
        var allowed = Math.Max(0, (c.MaxHashtags ?? c.RecommendedHashtags.Max) - bodyTags);
        var tags = d.Hashtags.Select(PostText.Normalize).Where(t => t.Length > 0).Distinct().Take(allowed).ToList();

        var tagsLength = tags.Count == 0 ? 0 : PostText.Length(PostText.Compose("", tags));
        if (PostText.Length(body) + tagsLength > c.MaxBodyLength)
        {
            body = PostText.Truncate(body, c.MaxBodyLength - tagsLength);
        }
        var title = c.MaxTitleLength is { } mt ? PostText.Truncate(d.Title ?? body, mt) : null;
        return (body, tags, title);
    }
}
