using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// LP の画像の候補を画像理解モデルに見せて、商品・サービスの特色が伝わる画像を選ぶ（構造化出力）。
/// 写真のほか、ソフトウェアの画面（スクリーンショット）も製品そのものとして選び、種類（写真・画面）を返す。
/// ロゴ・文字だけのバナーは選ばない。存在しない番号・重複は除く。
/// </summary>
public sealed class LpImageCurator(IModelRouter router, IPromptCatalog prompts) : ILpImageCurator
{
    public async Task<IReadOnlyList<LpImagePick>> PickAsync(WebPage page, IReadOnlyList<LpImageCandidate> candidates, int max,
        CancellationToken ct)
    {
        if (candidates.Count == 0 || max <= 0) return [];
        var client = router.Resolve(AiTaskType.Vision);
        var options = new AiCallContext(AiTaskType.Vision, null, new LpImagesStubPayload(candidates, max)).Apply();
        var system = (await prompts.RenderAsync(PromptKeys.LpImages, PromptLibrary.Values(("max", max)), ct)).Text;

        var content = new List<AIContent>
        {
            new TextContent($"LP：{PromptInjectionDetector.Fence($"{page.Title}\n{page.Description}")}\n画像の候補は {candidates.Count} 枚です。"),
        };
        foreach (var c in candidates)
        {
            var alt = string.IsNullOrWhiteSpace(c.Alt) ? "" : $"、代替テキスト：{PromptInjectionDetector.Fence(PostText.Truncate(c.Alt, 80))}";
            content.Add(new TextContent($"候補 {c.Index}（元の大きさ {c.Width}×{c.Height}{alt}）"));
            content.Add(new DataContent(c.Thumbnail, c.ThumbnailMime));
        }
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpImagePicksDraft>(client,
            [new(ChatRole.System, system), new(ChatRole.User, content)], options, ct);

        var known = candidates.Select(c => c.Index).ToHashSet();
        return [.. (draft.Picks ?? [])
            .Where(p => known.Contains(p.Index))
            .DistinctBy(p => p.Index)
            .Take(max)
            .Select(p => new LpImagePick(p.Index, PostText.Truncate((p.Description ?? "").Trim(), 60),
                string.Equals(p.Kind?.Trim(), "screen", StringComparison.OrdinalIgnoreCase) ? LpSourceKind.Screen : LpSourceKind.Photo))];
    }
}
