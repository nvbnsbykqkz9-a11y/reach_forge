namespace ReachForge.Domain.Common;

/// <summary>全テーブル共通の列（RF-DES-001 6.2）：id(uuid v7), tenant_id, created_at, updated_at, row_version。</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>楽観排他用。保存のたびにインクリメントする。</summary>
    public long RowVersion { get; set; }
}
