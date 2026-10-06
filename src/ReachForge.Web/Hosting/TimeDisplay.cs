using System.Globalization;
using ReachForge.Application.Services;

namespace ReachForge.Web.Hosting;

/// <summary>日時の表示（テナント TZ・相対＋絶対：RF-UX-001 10.1「日時は相対＋絶対」）。</summary>
public sealed class TimeDisplay(SchedulingService scheduling, TimeProvider clock)
{
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");
    private TimeZoneInfo? _tz;

    public async Task<TimeZoneInfo> ZoneAsync() => _tz ??= await scheduling.TenantTimeZoneAsync(CancellationToken.None);

    public TimeZoneInfo Zone => _tz ?? TimeZoneInfo.Utc;

    public string ZoneLabel => Zone.Id is "Asia/Tokyo" or "Tokyo Standard Time" ? "日本時間（UTC+9）" : Zone.DisplayName;

    public DateTimeOffset Now => clock.GetUtcNow();

    public DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;

    public DateTimeOffset FromLocal(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>例：10/10（土）09:00</summary>
    public string Short(DateTimeOffset utc)
    {
        var l = ToLocal(utc);
        return $"{l:M/d}（{Ja.DateTimeFormat.GetShortestDayName(l.DayOfWeek)}）{l:HH:mm}";
    }

    /// <summary>例：あと2時間（今日 12:00）</summary>
    public string Relative(DateTimeOffset utc)
    {
        var diff = utc - Now;
        var local = ToLocal(utc);
        var today = ToLocal(Now).Date;
        var day = local.Date == today ? "今日" : local.Date == today.AddDays(1) ? "明日" : $"{local:M/d}";
        var rel = Math.Abs(diff.TotalMinutes) < 60 ? $"{Math.Abs((int)diff.TotalMinutes)}分"
            : Math.Abs(diff.TotalHours) < 24 ? $"{Math.Abs((int)diff.TotalHours)}時間"
            : $"{Math.Abs((int)diff.TotalDays)}日";
        return diff >= TimeSpan.Zero ? $"あと{rel}（{day} {local:HH:mm}）" : $"{rel}前（{day} {local:HH:mm}）";
    }
}
