using ReachForge.Domain.Enums;

namespace ReachForge.Application.Abstractions;

/// <summary>
/// 現在の要求のテナント・ワークスペース・利用者。Web では認証情報から、Worker ではジョブ対象から設定する。
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid WorkspaceId { get; }
    string UserName { get; }
    Role Role { get; }

    /// <summary>テナントを横断する処理（Worker）。グローバルクエリフィルタを無効化する。</summary>
    bool IsSystem { get; }
}

/// <summary>Worker やテストで明示的にテナントを切り替えるための実装。</summary>
public sealed class MutableTenantContext : ITenantContext
{
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserName { get; set; } = "system";
    public Role Role { get; set; } = Role.Owner;
    public bool IsSystem { get; set; }
}
