using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Services;

/// <summary>
/// 承認者向け「確認ポイント要約」（F-07-1 / RF-UX-001 SCR-08）：伝えたいこと／要確認／前回指摘への対応。
/// 「伝えたいこと」だけを AI が文章化し、確認事項はシステムが判定した結果をそのまま示す。
/// </summary>
public sealed class ApprovalSummaryService(IAppDbContext db, IModelRouter router) : IApprovalSummaryService
{
    public async Task<IReadOnlyList<string>> SummarizeAsync(Guid variantId, CancellationToken ct)
    {
        var v = await db.PostVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var post = await db.MasterPosts.FirstAsync(p => p.Id == v.MasterPostId, ct);
        var lines = new List<string>();

        string message;
        try
        {
            var client = router.Resolve(AiTaskType.Summarize);
            var options = new AiCallContext(AiTaskType.Summarize, v.AiGenerationId,
                new DigestStubPayload(post.Title, v.Body)).Apply();
            var (digest, _) = await CopyGenerationService.GetStructuredAsync<DigestResult>(client,
                [
                    new(ChatRole.System, PromptLibrary.DigestSystem),
                    new(ChatRole.User, Domain.Guardrails.PromptInjectionDetector.Fence($"{post.Title}\n{v.Body}")),
                ], options, ct);
            message = digest.Message;
        }
        catch (DomainException)
        {
            message = post.Title;
        }
        lines.Add($"伝えたいこと：{message}");

        var issues = v.GuardrailFindings.Where(f => f.Level >= GuardrailLevel.Warning).ToList();
        lines.Add(issues.Count == 0
            ? "要確認：なし（確認事項はありません）"
            : $"要確認：{string.Join(" ／ ", issues.Select(f => f.Excerpt is null ? f.Message : $"「{f.Excerpt}」{(f.Suggestion is null ? "" : $"→「{f.Suggestion}」")}（{f.Reason}）"))}");

        var lastReject = await db.ApprovalActions
            .Where(a => a.PostVariantId == v.Id && a.Decision == ApprovalDecision.Rejected)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lastReject is not null)
        {
            lines.Add($"前回の差し戻し理由（{lastReject.Comment}）：再申請済み。変更点をご確認ください");
        }
        return lines;
    }
}
