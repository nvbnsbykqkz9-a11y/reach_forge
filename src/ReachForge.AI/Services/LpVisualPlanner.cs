using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// LP の内容・色と素材画像から、広告の世界観（雰囲気・色・背景の模様・映像の舞台）と、広告のビジュアル案
/// （切り口・見出し・画像生成と動画生成への指示）をつくる（構造化出力）。
/// 見出しは NG 語・規制表現・LP にない価格を確認し、問題があれば見出しを空にする（画像は見出しなしでつくる）。
/// </summary>
public sealed partial class LpVisualPlanner(IModelRouter router, IPromptCatalog prompts) : ILpVisualPlanner
{
    public const int MaxHeadline = 15;

    private const string DefaultImagePrompt = "A clean, appealing advertising photo of the product with soft natural light.";
    private const string DefaultMotion = "Slow push-in camera move.";
    private const string DefaultSetting = "a calm, modern space with soft cinematic light and gentle depth of field";

    public async Task<LpVisualPlan> PlanAsync(BrandContext brand, WebPage page, IReadOnlyList<LpSourceBrief> sources, CancellationToken ct)
    {
        if (sources.Count == 0) return new LpVisualPlan(Direction(null, brand, page), []);
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null,
            new LpVisualsStubPayload(page, [.. sources.Select(s => s.Description)], brand.Profile.BrandName)).Apply();
        var system = (await prompts.RenderAsync(PromptKeys.LpVisuals, PromptLibrary.LpVisualValues(brand, sources.Count), ct)).Text;
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpVisualsDraft>(client,
            [new(ChatRole.System, system), new(ChatRole.User, PromptLibrary.LpVisualUser(page, sources))], options, ct);

        var direction = Direction(draft.Direction, brand, page);
        var guard = brand.ToGuardrailContext();
        var prices = LandingPageVideoPlanner.Prices($"{page.Title}\n{page.Description}\n{page.Text}");
        var visuals = new List<LpVisualConcept>();
        foreach (var v in draft.Visuals ?? [])
        {
            if (v.SourceIndex < 0 || v.SourceIndex >= sources.Count || visuals.Any(x => x.SourceIndex == v.SourceIndex)) continue;
            var headline = PostText.Truncate((v.Headline ?? "").Trim().TrimEnd('。'), MaxHeadline);
            if (!IsSafe(headline, guard, prices)) headline = "";
            visuals.Add(new LpVisualConcept(v.SourceIndex,
                PostText.Truncate((v.Angle ?? "").Trim(), 12),
                headline,
                Clean(v.ImagePrompt, DefaultImagePrompt),
                Clean(v.MotionPrompt, DefaultMotion),
                sources[v.SourceIndex].Kind,
                Clean(v.BackdropPrompt, direction.Setting)));
        }
        // 案が足りない素材は、ひかえめな指示で補う（画像は必ず素材ごとにつくる）
        for (var i = 0; i < sources.Count; i++)
        {
            if (visuals.Any(v => v.SourceIndex == i)) continue;
            visuals.Add(new LpVisualConcept(i, "", "", DefaultImagePrompt, DefaultMotion, sources[i].Kind, direction.Setting));
        }
        return new LpVisualPlan(direction, [.. visuals.OrderBy(v => v.SourceIndex)]);
    }

    /// <summary>
    /// 世界観を整える：色は #RRGGBB だけを使い、足りなければ LP の色・ブランドの色で補う。模様は決まった種類のうちから（不明なら aurora）。
    /// </summary>
    internal static LpArtDirection Direction(LpDirectionDraft? draft, BrandContext brand, WebPage page)
    {
        var palette = (draft?.Palette ?? [])
            .Concat(page.Colors)
            .Concat(brand.Profile.BrandColors)
            .Select(c => (c ?? "").Trim())
            .Where(c => HexColor().IsMatch(c))
            .Select(c => c.ToUpperInvariant())
            .Distinct()
            .Take(4)
            .ToList();
        var motif = Enum.TryParse<BackdropMotif>(draft?.Motif?.Trim(), ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : BackdropMotif.Aurora;
        return new LpArtDirection
        {
            Mood = PostText.Truncate((draft?.Mood ?? "").Trim(), 30),
            Palette = palette,
            Motif = motif,
            Setting = Clean(draft?.Setting, DefaultSetting),
        };
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

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
