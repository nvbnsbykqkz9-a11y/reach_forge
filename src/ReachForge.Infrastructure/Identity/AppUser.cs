using Microsoft.AspNetCore.Identity;

namespace ReachForge.Infrastructure.Identity;

/// <summary>ログイン利用者（ASP.NET Core Identity）。所属・ロールは WorkspaceMember で管理する。</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>最後に開いていたワークスペース（ログイン時の既定）。</summary>
    public Guid? LastWorkspaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Cookie に載せるクレーム名。</summary>
public static class RfClaims
{
    public const string TenantId = "rf:tenant";
    public const string WorkspaceId = "rf:workspace";
    public const string Role = "rf:role";
    public const string DisplayName = "rf:name";
}
