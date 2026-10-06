using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Hosting;

/// <summary>
/// Web の要求コンテキスト。Blazor Server ではサーキット単位のスコープになる。
/// 認証基盤（ASP.NET Core Identity ＋ Entra External ID：RF-DES-001 3.3）導入までは、デモテナントで動作し、
/// 画面右上のメニューからロールを切り替えて権限マトリクスを確認できる。
/// </summary>
public sealed class WebTenantContext : ITenantContext
{
    public WebTenantContext(IConfiguration config)
    {
        UserName = config["Demo:UserName"] ?? "田中";
        Role = Enum.TryParse<Role>(config["Demo:Role"], out var r) ? r : Role.Owner;
    }

    public Guid TenantId { get; set; } = DemoSeeder.TenantId;
    public Guid WorkspaceId { get; set; } = DemoSeeder.WorkspaceId;
    public string UserName { get; set; }
    public Role Role { get; set; }
    public bool IsSystem => false;

    public event Action? Changed;

    public void SwitchRole(Role role, string userName)
    {
        Role = role;
        UserName = userName;
        Changed?.Invoke();
    }
}
