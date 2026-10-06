using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Services;

internal static class Audit
{
    /// <summary>監査ログに1件追加する（保存は呼び出し側の SaveChanges で行う）。</summary>
    public static void Record(this IAppDbContext db, ITenantContext ctx, string action, string targetType, Guid? targetId,
        string? detail = null, Guid? tenantId = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId ?? ctx.TenantId,
            Actor = ctx.UserName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
        });
}
