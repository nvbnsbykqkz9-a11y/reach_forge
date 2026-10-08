using System.Text.RegularExpressions;
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
/// LP から集客動画の企画と絵コンテをつくる（AiTaskType.Video のルート、構造化出力）。
/// 出力ガードレール：テロップ・ナレーション・投稿文を NG 語・規制表現で確認し、価格（円）は LP の本文にあるものだけを許す。
/// </summary>
public sealed partial class LandingPageVideoPlanner(IModelRouter router, IPromptCatalog prompts) : ILandingPageVideoPlanner
{
    private static readonly HashSet<string> s_roles = ["hook", "problem", "solution", "benefit", "proof", "offer", "cta"];

    public async Task<LandingPageVideoPlan> PlanAsync(BrandContext brand, WebPage page, int sceneCount, int targetSeconds, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Video);
        var options = new AiCallContext(AiTaskType.Video, null,
            new LandingPageStubPayload(page, sceneCount, targetSeconds, brand.Profile.BrandName)).Apply();
        var system = (await prompts.RenderAsync(PromptKeys.VideoLandingPage,
            PromptLibrary.LandingPageValues(brand, page, sceneCount, targetSeconds), ct)).Text;
        var (d, _) = await CopyGenerationService.GetStructuredAsync<LandingPlanDraft>(client,
            [new(ChatRole.System, system), new(ChatRole.User, PromptLibrary.LandingPageUser(page))], options, ct);

        var images = page.ImageList.Count;
        var scenes = (d.Scenes ?? []).Take(sceneCount)
            .Select(s => new LandingPageScene(
                s_roles.Contains(s.Role?.Trim().ToLowerInvariant() ?? "") ? s.Role!.Trim().ToLowerInvariant() : "benefit",
                PostText.Truncate(s.Caption?.Trim() ?? "", TextOverlay.MaxHeadline),
                s.Narration?.Trim() ?? "",
                Math.Clamp(s.Seconds, 2, 8),
                s.ImageIndex is { } i && i >= 0 && i < images ? i : null)
            {
                Points = [.. (s.Points ?? []).Select(x => PostText.Truncate((x ?? "").Trim().TrimEnd('。'), LandingPageScene.MaxPointLength))
                    .Where(x => x.Length > 0).Distinct().Take(LandingPageScene.MaxPoints)],
            })
            .ToList();
        if (scenes.Count < 2) throw new AiUnavailableException("動画の構成をつくれませんでした。もう一度お試しください。");

        var plan = new LandingPageVideoPlan(
            string.IsNullOrWhiteSpace(d.Title) ? PostText.Truncate(page.Title, 30) : PostText.Truncate(d.Title.Trim(), 30),
            d.Product?.Trim() ?? "",
            d.Target?.Trim() ?? "",
            (d.Benefits ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Take(3).ToList(),
            d.Offer?.Trim() ?? "",
            d.CallToAction?.Trim() ?? "",
            scenes,
            d.PostText?.Trim() ?? "",
            (d.Hashtags ?? []).Select(PostText.Normalize).Where(h => h.Length > 0).Distinct().Take(5).ToList(),
            d.HookMotion?.Trim() ?? "");
        Check(plan, page, brand);
        return plan;
    }

    /// <summary>出力の確認。NG 語・規制表現（エラー）と、LP にない価格は使わない。</summary>
    internal static void Check(LandingPageVideoPlan plan, WebPage page, BrandContext brand)
    {
        var guard = brand.ToGuardrailContext();
        var texts = plan.Scenes.SelectMany(s => new[] { s.Caption, s.Narration }.Concat(s.Points)).Append(plan.PostText).Append(plan.Offer).ToList();
        if (texts.Any(t => GuardrailChecker.CheckContent(t, guard).Findings.Any(f => f.Level == GuardrailLevel.Error
                                                                                     || f.Code is GuardrailCodes.RegulatedExpression or GuardrailCodes.PharmaExpression)))
        {
            throw new AiSafetyBlockedException("ブランドのNGワード・規制表現");
        }
        var source = Prices($"{page.Title}\n{page.Description}\n{page.Text}");
        var unknown = texts.SelectMany(Prices).Where(price => !source.Contains(price)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            throw new AiSafetyBlockedException($"LP にない価格（{string.Join("、", unknown.Select(p => p + "円"))}）");
        }
    }

    /// <summary>文中の価格（「1,980円」「¥1980」「１９８０円」）を、カンマのない半角数字で返す。</summary>
    internal static HashSet<string> Prices(string text) =>
        PriceRegex().Matches(Normalize(text)).Select(m => m.Groups["n"].Value.Replace(",", "")).ToHashSet();

    /// <summary>全角数字・カンマ・「￥」をそろえる。</summary>
    private static string Normalize(string s) =>
        new string(s.Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c).ToArray())
            .Replace("，", ",").Replace("￥", "¥");

    [GeneratedRegex(@"¥\s*(?<n>\d[\d,]*)|(?<n>\d[\d,]*)\s*円")]
    private static partial Regex PriceRegex();
}
