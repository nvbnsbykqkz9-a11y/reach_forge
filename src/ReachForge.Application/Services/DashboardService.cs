using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record KpiValue(string Label, double Value, double? Previous, string Format, string? Help = null)
{
    public double? Change => Previous is { } p ? MetricsCalculator.ChangeRatio(Value, p) : null;
}

public enum ActionSeverity { Error = 0, Deadline = 1, Urgent = 2, Other = 3 }

public sealed record ActionItem(ActionSeverity Severity, string Message, string Href, string LinkText);

public sealed record DashboardSummary(
    IReadOnlyList<KpiValue> Kpis,
    IReadOnlyList<ActionItem> ActionItems,
    IReadOnlyList<CalendarEntry> Upcoming,
    IReadOnlyList<string> Suggestions,
    bool PublishingPaused);

/// <summary>ダッシュボード（SCR-02）。</summary>
public sealed class DashboardService(
    IAppDbContext db,
    ITenantContext tenant,
    SchedulingService scheduling,
    ICreditService credits,
    TimeProvider clock)
{
    public async Task<DashboardSummary> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tz = await scheduling.TenantTimeZoneAsync(ct);
        var weekStart = StartOfWeek(now, tz);

        var kpis = await KpisAsync(weekStart, weekStart.AddDays(7), weekStart.AddDays(-7), ct);
        var actions = await ActionItemsAsync(now, ct);
        var upcoming = (await scheduling.CalendarAsync(now, now.AddDays(7), ct))
            .Where(e => e.Status is VariantStatus.Scheduled or VariantStatus.InReview)
            .Take(10)
            .ToList();
        var suggestions = await SuggestionsAsync(tz, ct);
        return new DashboardSummary(kpis, actions, upcoming, suggestions, await scheduling.IsPausedAsync(ct));
    }

    private async Task<IReadOnlyList<KpiValue>> KpisAsync(DateTimeOffset from, DateTimeOffset to, DateTimeOffset prevFrom,
        CancellationToken ct)
    {
        var variantIds = db.PostVariants.Where(v => v.WorkspaceId == tenant.WorkspaceId).Select(v => v.Id);
        var metrics = (await db.PostMetrics
                .Where(m => variantIds.Contains(m.PostVariantId) && m.PostedAt >= prevFrom && m.PostedAt < to)
                .ToListAsync(ct))
            .GroupBy(m => m.PostVariantId)
            .Select(g => g.MaxBy(m => m.CapturedAt)!)
            .ToList();

        var current = metrics.Where(m => m.PostedAt >= from).ToList();
        var previous = metrics.Where(m => m.PostedAt < from).ToList();

        return
        [
            new("表示回数", current.Sum(m => m.Impressions), previous.Sum(m => m.Impressions), "N0"),
            new("反応の割合", Rate(current), Rate(previous), "P1",
                "（いいね＋コメント＋シェア＋保存）÷ 表示回数"),
            new("リンクのクリック", current.Sum(m => m.LinkClicks), previous.Sum(m => m.LinkClicks), "N0"),
            new("フォロワー増加", current.Sum(m => m.Follows), previous.Sum(m => m.Follows), "+#,0;-#,0;0"),
        ];

        static double Rate(List<PostMetric> ms)
        {
            var imp = ms.Sum(m => m.Impressions);
            return imp == 0 ? 0 : (double)ms.Sum(MetricsCalculator.Engagements) / imp;
        }
    }

    /// <summary>要対応：重要度順（エラー → 期限が近い承認 → 急ぎ → その他）、最大5件。</summary>
    private async Task<IReadOnlyList<ActionItem>> ActionItemsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var items = new List<ActionItem>();
        var channels = await db.Channels.Where(c => c.WorkspaceId == tenant.WorkspaceId && c.Status != ChannelStatus.Revoked)
            .ToListAsync(ct);
        foreach (var c in channels.Where(c => c.Status != ChannelStatus.Active))
        {
            items.Add(new(ActionSeverity.Error, $"{PlatformCatalog.Get(c.Platform).DisplayName} の再接続が必要です",
                "/settings/channels", "再接続"));
        }
        foreach (var c in channels.Where(c => c.Status == ChannelStatus.Active && c.IsTokenExpiringSoon(now)))
        {
            items.Add(new(ActionSeverity.Other, $"{PlatformCatalog.Get(c.Platform).DisplayName} はまもなく再接続が必要です",
                "/settings/channels", "確認"));
        }

        var failed = await db.PostVariants.CountAsync(v => v.WorkspaceId == tenant.WorkspaceId && v.Status == VariantStatus.Failed, ct);
        if (failed > 0) items.Add(new(ActionSeverity.Error, $"公開に失敗した投稿 {failed}件", "/calendar?view=list", "確認"));

        var inReview = await db.PostVariants
            .Where(v => v.WorkspaceId == tenant.WorkspaceId && v.Status == VariantStatus.InReview)
            .ToListAsync(ct);
        if (inReview.Count > 0)
        {
            var today = inReview.Count(v => v.RequestedPublishAt is { } at && at - now < TimeSpan.FromHours(24));
            items.Add(new(today > 0 ? ActionSeverity.Deadline : ActionSeverity.Other,
                today > 0 ? $"承認待ち {inReview.Count}件（うち{today}件は24時間以内に予約）" : $"承認待ち {inReview.Count}件",
                "/approvals", "確認"));
        }

        var onHold = await db.PostVariants.CountAsync(v => v.WorkspaceId == tenant.WorkspaceId && v.Status == VariantStatus.OnHold, ct);
        if (onHold > 0) items.Add(new(ActionSeverity.Deadline, $"保留中の投稿 {onHold}件", "/calendar?view=list", "確認"));

        var account = await credits.GetAccountAsync(ct);
        if (account.IsLow)
        {
            items.Add(new(ActionSeverity.Other, $"クレジット残り {account.RemainingRatio:P0}", "/settings/usage", "プラン"));
        }

        return items.OrderBy(i => i.Severity).Take(5).ToList();
    }

    /// <summary>AI からの提案（最大3件）。根拠を1文で添える。数値はシステムが計算した値のみ使う。</summary>
    private async Task<IReadOnlyList<string>> SuggestionsAsync(TimeZoneInfo tz, CancellationToken ct)
    {
        var suggestions = new List<string>();
        var best = await scheduling.BestTimesAsync(null, ct);
        if (best.FirstOrDefault() is { } top)
        {
            suggestions.Add($"{DayLabel(top.Day)}曜{top.Hour}時の投稿がおすすめです。{top.Rationale}。");
        }

        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);
        foreach (var (date, name) in SeasonalEvents.Upcoming(DateOnly.FromDateTime(local.Date), 30).Take(2))
        {
            suggestions.Add($"{date:M/d} {name}の投稿を準備しましょう。");
        }
        return suggestions.Take(3).ToList();
    }

    public static string DayLabel(DayOfWeek d) => "日月火水木金土"[(int)d].ToString();

    private static DateTimeOffset StartOfWeek(DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz).Date;
        var monday = local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
        return new DateTimeOffset(monday, tz.GetUtcOffset(monday)).ToUniversalTime();
    }
}

/// <summary>季節イベント・記念日辞書の初期値（F-12 イベント辞書の一部）。</summary>
public static class SeasonalEvents
{
    private static readonly (int Month, int Day, string Name)[] s_events =
    [
        (1, 1, "お正月"), (2, 3, "節分"), (2, 14, "バレンタインデー"), (3, 3, "ひな祭り"), (3, 14, "ホワイトデー"),
        (4, 1, "新年度"), (5, 5, "こどもの日"), (6, 1, "衣替え"), (7, 7, "七夕"), (8, 11, "山の日"),
        (9, 15, "お月見シーズン"), (10, 31, "ハロウィン"), (11, 11, "ポッキー＆プリッツの日"), (11, 22, "いい夫婦の日"),
        (12, 25, "クリスマス"), (12, 31, "大晦日"),
    ];

    public static IEnumerable<(DateOnly Date, string Name)> Upcoming(DateOnly today, int withinDays) =>
        s_events
            .Select(e => (Date: NextDate(today, e.Month, e.Day), e.Name))
            .Where(e => e.Date.DayNumber - today.DayNumber <= withinDays)
            .OrderBy(e => e.Date);

    private static DateOnly NextDate(DateOnly today, int month, int day)
    {
        var d = new DateOnly(today.Year, month, day);
        return d < today ? d.AddYears(1) : d;
    }
}
