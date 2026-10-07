using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Credits;

namespace ReachForge.Application.Services;

/// <summary>
/// CreditResetJob（14章：毎月1日 00:00）。テナントのタイムゾーンで月が替わったアカウントに月次クレジットを付与し直す。
/// 付与済みの月（PeriodStart）は二度とリセットしないため、何度実行しても結果は同じ。
/// </summary>
public sealed class CreditResetService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<int> ResetDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("ResetDueAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();
        var zones = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.TimeZoneId, ct);
        var reset = 0;
        foreach (var account in await db.CreditAccounts.ToListAsync(ct))
        {
            var tz = SchedulingService.FindTimeZone(zones.GetValueOrDefault(account.TenantId, "Asia/Tokyo"));
            var local = TimeZoneInfo.ConvertTime(now, tz);
            var periodStart = new DateOnly(local.Year, local.Month, 1);
            if (account.PeriodStart >= periodStart) continue;
            var previous = account.ConsumedThisPeriod;
            account.ResetPeriod(periodStart);
            db.Record(tenant, "credits.reset", nameof(CreditAccount), account.Id,
                $"{periodStart:yyyy-MM}：{account.MonthlyGrant} を付与（前月の消費 {previous}）", account.TenantId);
            reset++;
        }
        await db.SaveChangesAsync(ct);
        return reset;
    }
}
