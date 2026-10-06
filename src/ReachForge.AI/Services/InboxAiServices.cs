using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Common;
using ReachForge.Domain.Engagement;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Services;

/// <summary>受信メッセージの分類（AiTaskType.Classify）。AI が使えない場合はキーワード分類で代替する。</summary>
public sealed class InboxClassifier(IModelRouter router, ILogger<InboxClassifier> log) : IInboxClassifier
{
    public async Task<InboxLabels> ClassifyAsync(string maskedText, CancellationToken ct)
    {
        var fallback = InboxHeuristics.Classify(maskedText);
        try
        {
            var client = router.Resolve(AiTaskType.Classify);
            var options = new AiCallContext(AiTaskType.Classify, null, new ClassifyStubPayload(maskedText)).Apply();
            var (draft, _) = await CopyGenerationService.GetStructuredAsync<ClassificationDraft>(client,
                [new(ChatRole.System, PromptLibrary.ClassifySystem), new(ChatRole.User, Domain.Guardrails.PromptInjectionDetector.Fence(maskedText))],
                options, ct);
            var labels = new InboxLabels(
                Parse(draft.Sentiment, fallback.Sentiment),
                Parse(draft.Intent, fallback.Intent),
                Parse(draft.Urgency, fallback.Urgency),
                Parse(draft.Sensitive, fallback.Sensitive),
                string.IsNullOrWhiteSpace(draft.Language) ? fallback.Language : draft.Language.Trim().ToLowerInvariant()[..Math.Min(2, draft.Language.Trim().Length)]);
            // 安全側：キーワードで人の対応が必要と判断したものは、AI が none と答えても人に回す
            if (labels.Sensitive == SensitiveTopic.None && fallback.Sensitive != SensitiveTopic.None)
            {
                labels = labels with { Sensitive = fallback.Sensitive, Urgency = Urgency.High };
            }
            return labels;
        }
        catch (DomainException ex)
        {
            log.LogWarning(ex, "Inbox classification fell back to keyword rules");
            return fallback;
        }
    }

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value?.Trim(), ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : fallback;
}

/// <summary>返信案（AiTaskType.Reply）。根拠の id を検証し、存在しない根拠は外す。</summary>
public sealed class ReplySuggester(IModelRouter router) : IReplySuggester
{
    public async Task<IReadOnlyList<ReplyDraft>> SuggestAsync(ReplyRequest request, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Reply);
        var options = new AiCallContext(AiTaskType.Reply, null, new ReplyStubPayload(request)).Apply();
        var (batch, _) = await CopyGenerationService.GetStructuredAsync<ReplyBatch>(client,
            [new(ChatRole.System, PromptLibrary.ReplySystem(request.Brand)), new(ChatRole.User, PromptLibrary.ReplyUser(request))],
            options, ct);

        var sources = new Dictionary<string, ReplySource>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hit, i) in request.Knowledge.Select((h, i) => (h, i)))
        {
            sources[$"K{i + 1}"] = new ReplySource("faq", hit.Entry.Id, $"FAQ「{hit.Entry.Question}」");
        }
        foreach (var (p, i) in request.Brand.Products.Take(5).Select((p, i) => (p, i)))
        {
            sources[$"P{i + 1}"] = new ReplySource("product", p.Id, $"商品「{p.Name}」");
        }
        return (batch.Replies ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Take(3)
            .Select(r => new ReplyDraft(r.Text.Trim(),
                (r.Sources ?? []).Select(s => sources.GetValueOrDefault(s.Trim())).OfType<ReplySource>().DistinctBy(s => s.Id).ToList()))
            .ToList();
    }
}
