using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>SNS 非依存の元コンテンツ。ここから各チャネル用のバリアントを派生する。</summary>
public sealed class MasterPost : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Title { get; set; }
    public PostObjective Objective { get; set; } = PostObjective.Awareness;

    /// <summary>入力されたテーマ（スタジオの「何を伝えますか？」）。</summary>
    public string Theme { get; set; } = "";

    /// <summary>SNS 非依存の主訴求（本文）。</summary>
    public string CoreMessage { get; set; } = "";
    public string Cta { get; set; } = "";
    public List<string> Hashtags { get; set; } = [];
    public List<Guid> ProductIds { get; set; } = [];

    /// <summary>添付メディア（元画像、順序付き）。SNS 別の比率に変換した派生画像はバリアント側に持つ。</summary>
    public List<Guid> MediaAssetIds { get; set; } = [];
    public Guid? CampaignId { get; set; }
    public string Language { get; set; } = "ja";

    /// <summary>生成元の AI 記録（来歴）。人が編集した場合は <see cref="IsAiEdited"/> を立てる。</summary>
    public Guid? AiGenerationId { get; set; }
    public bool IsAiEdited { get; set; }
    public string CreatedBy { get; set; } = "";
}
