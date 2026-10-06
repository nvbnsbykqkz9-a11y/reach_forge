using ReachForge.Application.Services;
using ReachForge.Domain.Engagement;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Ai;

/// <summary>受信メッセージの分類（F-09 処理 2、クレジット無料）。入力は個人情報をマスク済みの本文。</summary>
public interface IInboxClassifier
{
    Task<InboxLabels> ClassifyAsync(string maskedText, CancellationToken ct);
}

/// <summary>返信案の根拠（参照した FAQ・商品）。</summary>
public sealed record ReplySource(string Kind, Guid Id, string Label);

public sealed record ReplyDraft(string Text, IReadOnlyList<ReplySource> Sources);

public sealed record ReplyRequest(BrandContext Brand, string MaskedText, string? OriginalPost, IReadOnlyList<KnowledgeHit> Knowledge);

/// <summary>返信案（最大3案、F-09 処理 3）。根拠にない事実（価格・日時など）を書かせない。</summary>
public interface IReplySuggester
{
    Task<IReadOnlyList<ReplyDraft>> SuggestAsync(ReplyRequest request, CancellationToken ct);
}
