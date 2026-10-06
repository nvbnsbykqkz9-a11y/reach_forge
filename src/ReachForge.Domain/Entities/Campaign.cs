using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>キャンペーン（F-11）。広告扱いの場合は PR 表記を必須にする（ステマ規制）。</summary>
public sealed class Campaign : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>UTM の utm_campaign に使うコード。</summary>
    public required string Code { get; set; }

    public PostObjective Objective { get; set; } = PostObjective.Awareness;
    public bool IsAdvertisement { get; set; }
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
}
