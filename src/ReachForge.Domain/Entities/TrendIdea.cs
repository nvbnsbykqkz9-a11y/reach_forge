using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

public enum TrendSource : short { EventCalendar = 1, Web = 2, SocialSearch = 3, Manual = 4 }

public enum IdeaStatus : short { New = 1, Used = 2, Dismissed = 3 }

/// <summary>ネタ帳の1件（F-12）：話題・おすすめ投稿日・おすすめ形式・関連度・切り口3案。</summary>
public sealed class TrendIdea : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Topic { get; set; }
    public TrendSource Source { get; set; }
    public DateOnly RecommendedDate { get; set; }

    /// <summary>おすすめの形式（例：画像1枚、カルーセル、ショート動画）。</summary>
    public string Format { get; set; } = "";

    /// <summary>ブランド・お客様像との関連度（0〜1）。</summary>
    public double Relevance { get; set; }
    public List<string> Angles { get; set; } = [];
    public string Reason { get; set; } = "";
    public IdeaStatus Status { get; set; } = IdeaStatus.New;

    /// <summary>作成した日（同じ日に同じ話題を重複させない）。</summary>
    public DateOnly GeneratedOn { get; set; }
}
