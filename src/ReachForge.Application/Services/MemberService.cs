using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record InvitationLink(Invitation Invitation, string Token);

/// <summary>メンバー・権限（SCR-15）。招待はメールアドレスとロールを指定し、7日間有効のリンクを発行する。</summary>
public sealed class MemberService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<IReadOnlyList<WorkspaceMember>> ListAsync(CancellationToken ct) =>
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == tenant.WorkspaceId).OrderBy(m => m.Role).ThenBy(m => m.Email)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Invitation>> PendingInvitationsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return (await db.Invitations.Where(i => i.WorkspaceId == tenant.WorkspaceId && i.AcceptedAt == null && i.RevokedAt == null)
                .ToListAsync(ct))
            .Where(i => i.ExpiresAt > now)
            .OrderBy(i => i.Email)
            .ToList();
    }

    /// <summary>招待を作成する。オーナーの招待はオーナーのみ可能。</summary>
    public async Task<InvitationLink> InviteAsync(string email, Role role, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        email = email.Trim();
        if (!email.Contains('@') || email.Length > 256)
        {
            throw new DomainException(ErrorCodes.Validation, "メールアドレスの形式を確認してください。");
        }
        if (role == Role.Owner && tenant.Role != Role.Owner)
        {
            throw new ForbiddenException("オーナーの招待はオーナーのみ行えます。");
        }
        if (await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == tenant.WorkspaceId && m.Email == email, ct))
        {
            throw new DomainException(ErrorCodes.Validation, "このメールアドレスの人はすでにメンバーです。");
        }

        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var invitation = new Invitation
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Email = email,
            Role = role,
            TokenHash = Invitation.Hash(token),
            ExpiresAt = clock.GetUtcNow() + Invitation.Lifetime,
            InvitedBy = tenant.UserName,
        };
        db.Invitations.Add(invitation);
        db.Record(tenant, "member.invited", nameof(Invitation), invitation.Id, $"{email} as {role}");
        await db.SaveChangesAsync(ct);
        // TODO(12章): 招待メールの送信（現状は画面に表示したリンクを共有する）
        return new InvitationLink(invitation, token);
    }

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct) ?? throw new NotFoundException("招待");
        inv.RevokedAt = clock.GetUtcNow();
        db.Record(tenant, "member.invitation_revoked", nameof(Invitation), inv.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>ロールを変更する（権限変更は監査ログに記録）。最後のオーナーは変更できない。</summary>
    public async Task ChangeRoleAsync(Guid memberId, Role role, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.Id == memberId, ct) ?? throw new NotFoundException("メンバー");
        if ((role == Role.Owner || member.Role == Role.Owner) && tenant.Role != Role.Owner)
        {
            throw new ForbiddenException("オーナーの付与・変更はオーナーのみ行えます。");
        }
        await EnsureNotLastOwnerAsync(member, ct, changingTo: role);
        var before = member.Role;
        member.Role = role;
        db.Record(tenant, "member.role_changed", nameof(WorkspaceMember), member.Id, $"{member.Email}: {before} -> {role}");
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid memberId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.Id == memberId, ct) ?? throw new NotFoundException("メンバー");
        if (member.Role == Role.Owner && tenant.Role != Role.Owner) throw new ForbiddenException("オーナーの削除はオーナーのみ行えます。");
        await EnsureNotLastOwnerAsync(member, ct, changingTo: null);
        db.WorkspaceMembers.Remove(member);
        db.Record(tenant, "member.removed", nameof(WorkspaceMember), member.Id, member.Email);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNotLastOwnerAsync(WorkspaceMember member, CancellationToken ct, Role? changingTo)
    {
        if (member.Role != Role.Owner || changingTo == Role.Owner) return;
        var owners = await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == member.WorkspaceId && m.Role == Role.Owner, ct);
        if (owners <= 1)
        {
            throw new DomainException(ErrorCodes.Validation, "オーナーが1人もいなくなるため変更できません。先に別の人をオーナーにしてください。");
        }
    }
}
