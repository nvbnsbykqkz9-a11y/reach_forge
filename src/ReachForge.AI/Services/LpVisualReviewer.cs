using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// AI がつくった広告写真を画像理解モデルで確かめる（構造化出力）：参照した商品が変わっていないか・文字の崩れ・不自然さ・広告としての魅力。
/// 確認そのものができないとき（AI が使えない・応答が壊れているなど）は、生成を止めないよう合格として扱う。
/// </summary>
public sealed class LpVisualReviewer(IModelRouter router, IPromptCatalog prompts, ILogger<LpVisualReviewer> log) : ILpVisualReviewer
{
    public async Task<LpVisualReview> ReviewAsync(byte[] image, string mime, byte[]? reference, string? referenceMime, LpVisualConcept concept,
        CancellationToken ct)
    {
        try
        {
            var client = router.Resolve(AiTaskType.Vision);
            var options = new AiCallContext(AiTaskType.Vision, null, new LpReviewStubPayload(concept.Angle)).Apply();
            var system = (await prompts.RenderAsync(PromptKeys.LpReview,
                PromptLibrary.Values(("angle", string.IsNullOrWhiteSpace(concept.Angle) ? "商品の魅力" : concept.Angle)), ct)).Text;
            var content = new List<AIContent> { new TextContent("1枚目：AI がつくった広告写真"), new DataContent(image, mime) };
            if (reference is not null)
            {
                content.Add(new TextContent("2枚目：元にした商品・被写体の写真"));
                content.Add(new DataContent(reference, referenceMime ?? "image/png"));
            }
            var (draft, _) = await CopyGenerationService.GetStructuredAsync<LpReviewDraft>(client,
                [new(ChatRole.System, system), new(ChatRole.User, content)], options, ct);
            var score = Math.Clamp(draft.Score, 1, 5);
            var fix = (draft.Fix ?? "").ReplaceLineEndings(" ").Trim();
            // 作り直しの指示：英語の指示があればそれを、なければ問題点をそのまま渡す
            return new LpVisualReview(draft.Approved && score >= 4, score,
                PostText.Truncate(fix.Length > 0 ? fix : (draft.Problems ?? "").Trim(), 300));
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Key visual review failed; accepting the image as is");
            return LpVisualReview.Skipped;
        }
    }
}
