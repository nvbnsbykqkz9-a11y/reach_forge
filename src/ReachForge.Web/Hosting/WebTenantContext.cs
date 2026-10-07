using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Hosting;

/// <summary>
/// Web の要求コンテキスト。ログイン利用者のクレーム（テナント・ワークスペース）から作り、
/// ロールは要求（Blazor ではサーキット）ごとに WorkspaceMember から解決する。メンバーから外された人は即時にアクセスできなくなる。
/// </summary>
public sealed class WebTenantContext(
    IHttpContextAccessor http,
    IServiceProvider services,
    DbContextOptions<ReachForgeDbContext> dbOptions,
    TimeProvider clock) : ITenantContext
{
    private bool _resolved;
    private Guid _tenantId, _workspaceId, _userId;
    private string _userName = "";
    private Role _role = Role.Viewer;

    public Guid TenantId { get { Resolve(); return _tenantId; } }
    public Guid WorkspaceId { get { Resolve(); return _workspaceId; } }
    public Guid UserId { get { Resolve(); return _userId; } }
    public string UserName { get { Resolve(); return _userName; } }
    public Role Role { get { Resolve(); return _role; } }
    public bool IsSystem => false;
    public bool IsAuthenticated { get { Resolve(); return _tenantId != Guid.Empty; } }

    /// <summary>MFA を設定済みか（ログイン時点）。</summary>
    public bool MfaEnabled { get; private set; }

    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        var user = Principal();
        if (user?.Identity?.IsAuthenticated != true) return;
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(user.FindFirstValue(RfClaims.TenantId), out var tenantId) ||
            !Guid.TryParse(user.FindFirstValue(RfClaims.WorkspaceId), out var workspaceId))
        {
            return;
        }

        // API キー：ロールはキーに付けたもの（認証ハンドラが失効・期限を確認済み）
        if (user.FindFirstValue(RfClaims.ApiKeyId) is not null)
        {
            if (!Enum.TryParse<Role>(user.FindFirstValue(RfClaims.Role), out var keyRole)) return;
            _userId = userId;
            _tenantId = tenantId;
            _workspaceId = workspaceId;
            _role = keyRole;
            _userName = user.FindFirstValue(RfClaims.DisplayName) ?? "APIキー";
            MfaEnabled = true;
            return;
        }

        using var db = new ReachForgeDbContext(dbOptions, new MutableTenantContext { IsSystem = true }, null, clock);
        var member = db.WorkspaceMembers.AsNoTracking()
            .FirstOrDefault(m => m.UserId == userId && m.WorkspaceId == workspaceId && m.TenantId == tenantId);
        if (member is null) return; // 所属が外された

        _userId = userId;
        _tenantId = tenantId;
        _workspaceId = workspaceId;
        _role = member.Role;
        _userName = user.FindFirstValue(RfClaims.DisplayName) is { Length: > 0 } name ? name : member.Email;
        MfaEnabled = db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TwoFactorEnabled).FirstOrDefault();
    }

    private ClaimsPrincipal? Principal()
    {
        if (http.HttpContext?.User is { Identity.IsAuthenticated: true } httpUser) return httpUser;
        try
        {
            var task = services.GetService<AuthenticationStateProvider>()?.GetAuthenticationStateAsync();
            return task is { IsCompletedSuccessfully: true } ? task.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            return null; // Blazor 以外の要求
        }
    }
}
