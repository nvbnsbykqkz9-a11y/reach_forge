namespace ReachForge.Application.Services;

public static class TimeZones
{
    /// <summary>タイムゾーンを探す（見つからなければ UTC）。</summary>
    public static TimeZoneInfo Find(string? id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id ?? "Asia/Tokyo", out var tz) ? tz : TimeZoneInfo.Utc;
}
