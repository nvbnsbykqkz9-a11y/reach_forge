using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Identity;

public sealed record RegisterCommand(string CompanyName, string WorkspaceName, string DisplayName, string Email, string Password);

public sealed record MembershipInfo(Guid MemberId, Guid TenantId, Guid WorkspaceId, string WorkspaceName, string BrandColor, Role Role);

/// <summary>
/// アカウントの作成・招待の受諾・所属の解決（SCR-01 / SCR-15）。
/// ログイン前の処理はテナントが決まっていないため、テナント横断のシステムコンテキストで DB を扱う。
/// </summary>
public sealed class AccountService(
    IServiceProvider services,
    DbContextOptions<ReachForgeDbContext> dbOptions,
    TimeProvider clock)
{
    private UserManager<AppUser> Users => services.GetRequiredService<UserManager<AppUser>>();

    private ReachForgeDbContext SystemDb() =>
        new(dbOptions, new MutableTenantContext { IsSystem = true, UserName = "system" }, null, clock);

    /// <summary>新規登録：テナント・ワークスペース・オーナーを作成する（オンボーディング手順1）。</summary>
    public async Task<AppUser> RegisterAsync(RegisterCommand cmd, CancellationToken ct)
    {
        var tenant = new Tenant { Name = cmd.CompanyName.Trim() };
        var user = new AppUser
        {
            UserName = cmd.Email.Trim(),
            Email = cmd.Email.Trim(),
            DisplayName = cmd.DisplayName.Trim(),
            TenantId = tenant.Id,
            CreatedAt = clock.GetUtcNow(),
        };
        await EnsureSucceededAsync(Users.CreateAsync(user, cmd.Password));

        try
        {
            await using var db = SystemDb();
            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = string.IsNullOrWhiteSpace(cmd.WorkspaceName) ? cmd.CompanyName.Trim() : cmd.WorkspaceName.Trim(),
            };
            db.Tenants.Add(tenant);
            db.Workspaces.Add(workspace);
            db.BrandProfiles.Add(new BrandProfile { TenantId = tenant.Id, WorkspaceId = workspace.Id, BrandName = workspace.Name });
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = user.Id, Email = user.Email!,
                DisplayName = user.DisplayName, Role = Role.Owner,
            });
            db.AuditLogs.Add(new AuditLog { TenantId = tenant.Id, Actor = user.Email!, Action = "tenant.registered", TargetType = nameof(Tenant), TargetId = tenant.Id });
            await db.SaveChangesAsync(ct);

            user.LastWorkspaceId = workspace.Id;
            await Users.UpdateAsync(user);
            return user;
        }
        catch
        {
            await Users.DeleteAsync(user);
            throw;
        }
    }

    public async Task<Invitation?> FindInvitationAsync(string token, CancellationToken ct)
    {
        await using var db = SystemDb();
        var hash = Invitation.Hash(token);
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        return inv is not null && inv.IsUsable(clock.GetUtcNow()) ? inv : null;
    }

    /// <summary>
    /// 招待を受諾する。同じメールアドレスの利用者がいればその人をメンバーに追加し、いなければパスワードを設定して作成する。
    /// 利用者は1つのテナントに属する（代理店は1テナント内で複数ワークスペースを持つ）。
    /// </summary>
    public async Task<AppUser> AcceptInvitationAsync(string token, string displayName, string? password, CancellationToken ct)
    {
        await using var db = SystemDb();
        var hash = Invitation.Hash(token);
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
                  ?? throw new DomainException("E-AUTH-410", "この招待リンクは使えません。招待した人にもう一度依頼してください。");
        inv.Accept(clock.GetUtcNow());

        var user = await Users.FindByEmailAsync(inv.Email);
        if (user is not null && user.TenantId != inv.TenantId)
        {
            throw new DomainException("E-AUTH-409", "このメールアドレスは別の会社のアカウントで使われています。別のメールアドレスで招待を受けてください。");
        }
        if (user is null)
        {
            if (string.IsNullOrEmpty(password)) throw new DomainException(ErrorCodes.Validation, "パスワードを入力してください。");
            user = new AppUser
            {
                UserName = inv.Email, Email = inv.Email, EmailConfirmed = true, // 招待メールの受信で確認済みとみなす
                DisplayName = displayName.Trim(), TenantId = inv.TenantId, CreatedAt = clock.GetUtcNow(),
            };
            await EnsureSucceededAsync(Users.CreateAsync(user, password));
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == inv.WorkspaceId && m.UserId == user.Id, ct))
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = inv.TenantId, WorkspaceId = inv.WorkspaceId, UserId = user.Id, Email = inv.Email,
                DisplayName = user.DisplayName, Role = inv.Role,
            });
        }
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = inv.TenantId, Actor = inv.Email, Action = "member.joined", TargetType = nameof(Invitation), TargetId = inv.Id,
            Detail = inv.Role.ToString(),
        });
        await db.SaveChangesAsync(ct);

        user.LastWorkspaceId = inv.WorkspaceId;
        await Users.UpdateAsync(user);
        return user;
    }

    public async Task<IReadOnlyList<MembershipInfo>> MembershipsAsync(Guid userId, CancellationToken ct)
    {
        await using var db = SystemDb();
        var members = await db.WorkspaceMembers.Where(m => m.UserId == userId).ToListAsync(ct);
        var wsIds = members.Select(m => m.WorkspaceId).ToList();
        var workspaces = await db.Workspaces.Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        return members
            .Where(m => workspaces.ContainsKey(m.WorkspaceId))
            .Select(m => new MembershipInfo(m.Id, m.TenantId, m.WorkspaceId, workspaces[m.WorkspaceId].Name,
                workspaces[m.WorkspaceId].BrandColor, m.Role))
            .OrderBy(m => m.WorkspaceName)
            .ToList();
    }

    /// <summary>パスワード再設定リンクの有効期間。</summary>
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// パスワード再設定のメールを送る。アカウントの有無は画面に出さない（メールアドレスの存在を推測させない）。
    /// パスワードを持たない外部 ID の利用者には送らない。
    /// </summary>
    public async Task RequestPasswordResetAsync(string email, Func<Guid, string, string> linkFor, CancellationToken ct)
    {
        var user = await Users.FindByEmailAsync(email.Trim());
        if (user is null || !await Users.HasPasswordAsync(user)) return;
        var token = await Users.GeneratePasswordResetTokenAsync(user);
        var code = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(token));
        await services.GetRequiredService<AccountNotifications>().PasswordResetAsync(user.Email!, linkFor(user.Id, code), ResetTokenLifetime, ct);
        await AuditAsync(user, "auth.password_reset_requested", null, ct);
    }

    /// <summary>新しいパスワードを設定する。成功したらロックを解除し、ほかの端末のログインを無効にする（セキュリティスタンプ更新）。</summary>
    public async Task ResetPasswordAsync(Guid userId, string code, string newPassword, CancellationToken ct)
    {
        var invalid = new DomainException("E-AUTH-410", "このリンクは使えません（期限切れ・使用済み）。もう一度、パスワードの再設定を依頼してください。");
        var user = await Users.FindByIdAsync(userId.ToString()) ?? throw invalid;
        string token;
        try
        {
            token = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(code));
        }
        catch (FormatException)
        {
            throw invalid;
        }
        var result = await Users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken")) throw invalid;
            throw new DomainException(ErrorCodes.Validation, string.Join(" ", result.Errors.Select(IdentityMessages.Translate)));
        }
        await Users.SetLockoutEndDateAsync(user, null);
        await Users.ResetAccessFailedCountAsync(user);
        await AuditAsync(user, "auth.password_reset", null, ct);
        await services.GetRequiredService<AccountNotifications>().PasswordChangedAsync(user.Email!, ct);
    }

    /// <summary>ログインがロックされた直後に本人へ知らせる（SCR-01）。</summary>
    public async Task NotifyLockedOutAsync(string email, CancellationToken ct)
    {
        var user = await Users.FindByEmailAsync(email.Trim());
        if (user?.LockoutEnd is not { } until || until <= clock.GetUtcNow()) return;
        // 1回のロックにつき1通（ロック中の再試行のたびに送らない）。このロックの開始以降に通知済みかを監査ログで確かめる
        var lockedSince = until - services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout.DefaultLockoutTimeSpan - TimeSpan.FromMinutes(1);
        await using (var db = SystemDb())
        {
            var notified = (await db.AuditLogs.AsNoTracking()
                    .Where(a => a.TargetId == user.Id && a.Action == "auth.locked_out").Select(a => a.CreatedAt).ToListAsync(ct))
                .Any(at => at >= lockedSince);
            if (notified) return;
        }
        await services.GetRequiredService<AccountNotifications>().LockedOutAsync(user.Email!, until, ct);
        await AuditAsync(user, "auth.locked_out", null, ct);
    }

    /// <summary>監査ログ（ログイン・MFA 設定などの認証イベント）。</summary>
    public async Task AuditAsync(AppUser user, string action, string? detail, CancellationToken ct)
    {
        await using var db = SystemDb();
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = user.TenantId, Actor = user.Email ?? user.Id.ToString(), Action = action, TargetType = "User",
            TargetId = user.Id, Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSucceededAsync(Task<IdentityResult> task)
    {
        var result = await task;
        if (result.Succeeded) return;
        throw new DomainException(ErrorCodes.Validation, string.Join(" ", result.Errors.Select(e => IdentityMessages.Translate(e))));
    }
}

/// <summary>Identity のエラーを利用者向けの日本語にする（RF-UX-001 10.1：原因＋解決策）。</summary>
public static class IdentityMessages
{
    public static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "このメールアドレスはすでに登録されています。ログインしてください。",
        "PasswordTooShort" => "パスワードは10文字以上にしてください。",
        "PasswordRequiresNonAlphanumeric" => "パスワードに記号を1文字以上含めてください。",
        "PasswordRequiresDigit" => "パスワードに数字を1文字以上含めてください。",
        "PasswordRequiresLower" => "パスワードに英小文字を1文字以上含めてください。",
        "PasswordRequiresUpper" => "パスワードに英大文字を1文字以上含めてください。",
        "InvalidEmail" => "メールアドレスの形式を確認してください。",
        _ => e.Description,
    };
}

/// <summary>Cookie に載せるクレーム（テナント・ワークスペース）。ロールは要求ごとに DB から解決する（権限変更を即時反映）。</summary>
public sealed class RfClaimsPrincipalFactory(UserManager<AppUser> users, IOptions<IdentityOptions> options, AccountService accounts)
    : UserClaimsPrincipalFactory<AppUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var memberships = await accounts.MembershipsAsync(user.Id, CancellationToken.None);
        var current = memberships.FirstOrDefault(m => m.WorkspaceId == user.LastWorkspaceId) ?? memberships.FirstOrDefault();
        identity.AddClaim(new Claim(RfClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(RfClaims.DisplayName, user.DisplayName));
        if (current is not null) identity.AddClaim(new Claim(RfClaims.WorkspaceId, current.WorkspaceId.ToString()));
        return identity;
    }
}
