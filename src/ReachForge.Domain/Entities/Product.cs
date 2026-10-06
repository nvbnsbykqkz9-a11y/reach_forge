using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

/// <summary>
/// 商品・サービス情報。価格・期間は事実性チェック（生成物との突合）に使う（F-03 業務ルール）。
/// </summary>
public sealed class Product : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public decimal? Price { get; set; }
    public DateOnly? AvailableFrom { get; set; }
    public DateOnly? AvailableUntil { get; set; }
    public string? Url { get; set; }
}
