using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Security;

public enum Permission
{
    ManageChannels,
    ViewChannels,
    ManageBrand,
    ViewBrand,
    Generate,
    SubmitForApproval,
    Approve,
    Schedule,
    ViewSchedule,
    PauseAllPublishing,
    ReplyInbox,
    ViewInbox,
    ViewAnalytics,
    ManageBilling,
    ViewBilling,
    ManageMembers,
    /// <summary>レポートの定期配信・ロゴの設定。</summary>
    ManageReports,
}

/// <summary>ロール別権限マトリクス（RF-DES-001 11.1）。</summary>
public static class RolePolicy
{
    public static bool Can(Role role, Permission permission) => permission switch
    {
        Permission.ManageChannels => role is Role.Owner or Role.Admin,
        Permission.ViewChannels => role is Role.Owner or Role.Admin or Role.Viewer,
        Permission.ManageBrand => role is Role.Owner or Role.Admin,
        Permission.ViewBrand => role is not Role.Viewer,
        Permission.Generate => role is Role.Owner or Role.Admin or Role.Editor,
        Permission.SubmitForApproval => role is Role.Owner or Role.Admin or Role.Editor,
        Permission.Approve => role is Role.Owner or Role.Admin or Role.Approver,
        Permission.Schedule => role is Role.Owner or Role.Admin or Role.Editor or Role.Approver,
        Permission.ViewSchedule => role is not Role.Responder,
        Permission.PauseAllPublishing => role is Role.Owner or Role.Admin,
        Permission.ReplyInbox => role is Role.Owner or Role.Admin or Role.Responder,
        Permission.ViewInbox => role is not Role.Viewer,
        Permission.ViewAnalytics => true,
        Permission.ManageBilling => role is Role.Owner,
        Permission.ViewBilling => role is Role.Owner or Role.Admin,
        Permission.ManageMembers => role is Role.Owner or Role.Admin,
        Permission.ManageReports => role is Role.Owner or Role.Admin or Role.Editor,
        _ => false,
    };

    public static void Demand(Role role, Permission permission)
    {
        if (!Can(role, permission))
        {
            throw new ForbiddenException($"{role.ToLabel()}のロールではこの操作はできません。");
        }
    }
}

public sealed class ForbiddenException(string message) : DomainException("E-AUTH-403", message);

public sealed class NotFoundException(string what) : DomainException(ErrorCodes.NotFound, $"{what}が見つかりません。");
