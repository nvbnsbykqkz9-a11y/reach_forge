using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// LP の文言を、動画生成 AI が描ける映像の描写（英語）に書き換える（構造化出力）。
/// 元にした LP の文言は、LP の本文に実際にあるものだけを残す（ないものは空にする）。場面は決まった4つだけ、各1つ。
/// </summary>
public sealed partial class LpVideoPromptWriter(IModelRouter router, IPromptCatalog prompts) : ILpVideoPromptWriter
{
    public static readonly IReadOnlyList<string> Slots = ["hook", "solution", "backdrop", "image"];

    public const int MaxPromptLength = 1200;

    public async Task<IReadOnlyList<LpVideoPrompt>> WriteAsync(BrandContext brand, WebPage page, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, null, new LpVideoPromptsStubPayload(page)).Apply();
        var system = (await prompts.RenderAsync(PromptKeys.LpVideoPrompts, PromptLibrary.BrandValues(brand), ct)).Text;
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpVideoPromptsDraft>(client,
            [new(ChatRole.System, system), new(ChatRole.User, PromptLibrary.LandingPageUser(page))], options, ct);

        var lp = Compact($"{page.Title}\n{page.Description}\n{page.Text}");
        return [.. (draft.Scenes ?? [])
            .Select(s => (Slot: (s.Slot ?? "").Trim().ToLowerInvariant(), Scene: s))
            .Where(x => Slots.Contains(x.Slot) && !string.IsNullOrWhiteSpace(x.Scene.Prompt))
            .DistinctBy(x => x.Slot)
            .OrderBy(x => Slots.ToList().IndexOf(x.Slot))
            .Select(x =>
            {
                var source = PostText.Truncate((x.Scene.SourceText ?? "").Trim().Trim('「', '」', '"'), 60);
                // LP にない言葉を「LP の文言」として見せない
                if (source.Length > 0 && !lp.Contains(Compact(source), StringComparison.Ordinal)) source = "";
                var prompt = x.Scene.Prompt!.ReplaceLineEndings(" ").Trim();
                return new LpVideoPrompt(x.Slot, PostText.Truncate((x.Scene.Title ?? "").Trim(), 15), source,
                    prompt.Length > MaxPromptLength ? prompt[..MaxPromptLength] : prompt);
            })];
    }

    /// <summary>比べるために空白・改行を除く。</summary>
    internal static string Compact(string text) => Whitespace().Replace(text, "");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
