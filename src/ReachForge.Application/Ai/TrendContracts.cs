using ReachForge.Application.Services;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Ai;

/// <summary>ネタの候補（イベント辞書・Web・SNS 検索などから集めたもの）。</summary>
public sealed record TrendCandidate(string Topic, DateOnly? Date, TrendSource Source, string? Snippet = null);

/// <summary>ネタの候補を集める（F-12 処理 1）。Web 検索・X 検索など従量課金の情報源は上限付きで追加する。</summary>
public interface ITrendSource
{
    Task<IReadOnlyList<TrendCandidate>> CollectAsync(DateOnly today, CancellationToken ct);
}

public sealed record ScoredIdea(string Topic, double Relevance, string Format, IReadOnlyList<string> Angles, string Reason, bool Sensitive,
    int DaysBefore);

/// <summary>Research Agent：候補をブランド・お客様像との関連度で採点し、切り口3案とおすすめ形式をつける（F-12 処理 2〜3）。</summary>
public interface ITrendIdeaWriter
{
    Task<IReadOnlyList<ScoredIdea>> ScoreAsync(BrandContext brand, IReadOnlyList<TrendCandidate> candidates, CancellationToken ct);
}
