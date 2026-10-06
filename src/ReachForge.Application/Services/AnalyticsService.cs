using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public enum CompareMode { PreviousPeriod = 0, PreviousYear = 1 }

/// <summary>分析の条件（RF-UX-001 SCR-10：期間・比較・SNS・キャンペーン）。</summary>
public sealed record AnalyticsFilter(DateTimeOffset From, DateTimeOffset To, SocialPlatform? Platform = null,
    Guid? CampaignId = null, CompareMode Compare = CompareMode.PreviousPeriod)
{
    public (DateTimeOffset From, DateTimeOffset To) ComparisonPeriod => Compare == CompareMode.PreviousYear
        ? (From.AddYears(-1), To.AddYears(-1))
        : (From - (To - From), From);
}

public sealed record KpiValue(string Label, double Value, double? Previous, string Format, string? Help = null)
{
    public double? Change => Previous is { } p ? MetricsCalculator.ChangeRatio(Value, p) : null;
}

public sealed record PostPerformance(Guid VariantId, Guid MasterPostId, string Title, SocialPlatform Platform,
    DateTimeOffset PostedAt, long Impressions, double? EngagementRate, int LinkClicks, bool IsAiGenerated,
    int BodyLength, bool HasImage, PostObjective Objective, Guid? ImageAssetId);

public sealed record PlatformBreakdown(SocialPlatform Platform, int Posts, long Impressions, double? EngagementRate, int LinkClicks);

public sealed record TrendPoint(DateOnly Date, long Impressions, long Engagements);

/// <summary>分析結果。数値はすべてシステムが計算し、AI には計算させない（F-10 処理 1）。</summary>
public sealed record AnalyticsData(
    AnalyticsFilter Filter,
    IReadOnlyList<KpiValue> Kpis,
    IReadOnlyList<PlatformBreakdown> Platforms,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<PostPerformance> Ranking,
    double[][] HeatMap,
    IReadOnlyList<BestTimeSlot> BestTimes,
    double? AiEngagementRate,
    double? ManualEngagementRate,
    DateTimeOffset? LastCapturedAt)
{
    /// <summary>ヒートマップの時間帯（3時間ごと）。</summary>
    public static readonly string[] HourBands = ["0-3時", "3-6時", "6-9時", "9-12時", "12-15時", "15-18時", "18-21時", "21-24時"];

    public static readonly DayOfWeek[] Days =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
}

/// <summary>分析（SCR-10）とダッシュボードの KPI。</summary>
public sealed class AnalyticsService(
    IAppDbContext db,
    ITenantContext tenant,
    SchedulingService scheduling)
{
    public async Task<AnalyticsData> BuildAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var tz = await scheduling.TenantTimeZoneAsync(ct);
        var kpis = await KpisAsync(filter, ct);
        var (variants, latest, masters) = await LoadAsync(filter, filter.From, filter.To, ct);

        var ranking = latest
            .Select(m =>
            {
                var v = variants[m.PostVariantId];
                var master = masters.GetValueOrDefault(v.MasterPostId);
                return new PostPerformance(v.Id, v.MasterPostId, master?.Title ?? "", m.Platform, m.PostedAt, m.Impressions,
                    MetricsCalculator.EngagementRate(m), m.LinkClicks, v.AiGenerationId is not null, PostText.Length(v.Body),
                    v.MediaAssetIds.Count > 0, master?.Objective ?? PostObjective.Awareness, v.MediaAssetIds.FirstOrDefault() is { } a && a != Guid.Empty ? a : null);
            })
            .OrderByDescending(p => p.EngagementRate ?? -1)
            .ThenByDescending(p => p.Impressions)
            .ToList();

        var platforms = latest.GroupBy(m => m.Platform)
            .Select(g => new PlatformBreakdown(g.Key, g.Count(), g.Sum(m => m.Impressions), Rate(g), g.Sum(m => m.LinkClicks)))
            .OrderByDescending(p => p.Impressions)
            .ToList();

        var trend = new List<TrendPoint>();
        var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(filter.From, tz).Date);
        var endDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(filter.To.AddTicks(-1), tz).Date);
        var byDay = latest.GroupBy(m => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.PostedAt, tz).Date)).ToDictionary(g => g.Key);
        for (var d = startDay; d <= endDay; d = d.AddDays(1))
        {
            trend.Add(byDay.TryGetValue(d, out var g)
                ? new TrendPoint(d, g.Sum(m => m.Impressions), g.Sum(MetricsCalculator.Engagements))
                : new TrendPoint(d, 0, 0));
        }

        var heat = AnalyticsData.Days.Select(_ => new double[AnalyticsData.HourBands.Length]).ToArray();
        foreach (var g in latest.GroupBy(m =>
                 {
                     var local = TimeZoneInfo.ConvertTime(m.PostedAt, tz);
                     return (Day: Array.IndexOf(AnalyticsData.Days, local.DayOfWeek), Band: local.Hour / 3);
                 }))
        {
            heat[g.Key.Day][g.Key.Band] = Math.Round((Rate(g) ?? 0) * 100, 2);
        }

        var ai = latest.Where(m => variants[m.PostVariantId].AiGenerationId is not null).ToList();
        var manual = latest.Where(m => variants[m.PostVariantId].AiGenerationId is null).ToList();
        var best = await scheduling.BestTimesAsync(null, ct);
        return new AnalyticsData(filter, kpis, platforms, trend, ranking, heat, best, Rate(ai), Rate(manual),
            latest.Count == 0 ? null : latest.Max(m => m.CapturedAt));
    }

    /// <summary>KPI（期間と比較期間）。</summary>
    public async Task<IReadOnlyList<KpiValue>> KpisAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var (prevFrom, prevTo) = filter.ComparisonPeriod;
        var (_, current, _) = await LoadAsync(filter, filter.From, filter.To, ct);
        var (_, previous, _) = await LoadAsync(filter, prevFrom, prevTo, ct);
        var followers = await FollowerGrowthAsync(filter, filter.From, filter.To, ct) ?? current.Sum(m => m.Follows);
        var previousFollowers = await FollowerGrowthAsync(filter, prevFrom, prevTo, ct) ?? previous.Sum(m => m.Follows);

        return
        [
            new("表示回数", current.Sum(m => m.Impressions), previous.Sum(m => m.Impressions), "N0",
                "SNSが数えた表示回数。表示回数を出さないSNSはリーチまたは再生数で代わりに数えます"),
            new("反応の割合", Rate(current) ?? 0, Rate(previous) ?? 0, "P1", "（いいね＋コメント＋シェア＋保存）÷ 表示回数"),
            new("リンクのクリック", current.Sum(m => m.LinkClicks), previous.Sum(m => m.LinkClicks), "N0",
                "投稿内のリンクが押された回数（送客率＝クリック ÷ 表示回数）"),
            new("フォロワー増加", followers, previousFollowers, "+#,0;-#,0;0", "期間末のフォロワー数 − 期間初のフォロワー数"),
        ];
    }

    /// <summary>
    /// AI に渡す根拠の数値（ファクト）。値はここで表示用に整形し、AI はこの文字列を引用するだけにする。
    /// </summary>
    public static IReadOnlyList<ReportFact> Facts(AnalyticsData data)
    {
        var facts = new List<ReportFact>();
        void Add(string label, string value) => facts.Add(new ReportFact($"F{facts.Count + 1}", label, value));
        foreach (var k in data.Kpis)
        {
            Add(k.Label, k.Value.ToString(k.Format, CultureInfo.InvariantCulture));
            if (k.Change is { } c) Add($"{k.Label}（比較期間比）", Signed(c));
        }
        foreach (var p in data.Platforms)
        {
            var name = PlatformCatalog.Get(p.Platform).DisplayName;
            Add($"{name}の投稿数", p.Posts.ToString(CultureInfo.InvariantCulture));
            Add($"{name}の表示回数", p.Impressions.ToString("N0", CultureInfo.InvariantCulture));
            if (p.EngagementRate is { } r) Add($"{name}の反応の割合", r.ToString("P1", CultureInfo.InvariantCulture));
        }
        foreach (var (post, rank) in data.Ranking.Take(3).Select((p, i) => (p, i + 1)))
        {
            Add($"上位{rank}位の投稿「{post.Title}」（{PlatformCatalog.Get(post.Platform).DisplayName}）の反応の割合",
                post.EngagementRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "—");
        }
        if (data.Ranking.Count > 3 && data.Ranking[^1] is { } worst)
        {
            Add($"最下位の投稿「{worst.Title}」（{PlatformCatalog.Get(worst.Platform).DisplayName}）の反応の割合",
                worst.EngagementRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "—");
        }
        if (data.AiEngagementRate is { } ai) Add("AI生成投稿の反応の割合", ai.ToString("P1", CultureInfo.InvariantCulture));
        if (data.ManualEngagementRate is { } manual) Add("手動投稿の反応の割合", manual.ToString("P1", CultureInfo.InvariantCulture));
        if (data.BestTimes.FirstOrDefault() is { SampleSize: > 0 } best)
        {
            Add("反応が多い曜日・時刻", $"{DashboardService.DayLabel(best.Day)}曜{best.Hour}時（実績{best.SampleSize}件）");
        }
        return facts;
    }

    /// <summary>投稿別の指標（CSV 出力、UTF-8 BOM 付き：Excel で文字化けしないように）。</summary>
    public async Task<byte[]> CsvAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var data = await BuildAsync(filter, ct);
        var tz = await scheduling.TenantTimeZoneAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("公開日時,SNS,件名,表示回数,反応の割合,リンクのクリック,本文の文字数,画像,AI生成");
        foreach (var p in data.Ranking.OrderBy(p => p.PostedAt))
        {
            sb.AppendLine(string.Join(',',
                TimeZoneInfo.ConvertTime(p.PostedAt, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                PlatformCatalog.Get(p.Platform).DisplayName,
                Csv(p.Title),
                p.Impressions.ToString(CultureInfo.InvariantCulture),
                p.EngagementRate?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "",
                p.LinkClicks.ToString(CultureInfo.InvariantCulture),
                p.BodyLength.ToString(CultureInfo.InvariantCulture),
                p.HasImage ? "あり" : "なし",
                p.IsAiGenerated ? "はい" : "いいえ"));
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Csv(string value)
    {
        // CSV インジェクション対策：数式として解釈される先頭文字を無害化する
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Signed(double ratio) =>
        (ratio >= 0 ? "+" : "") + ratio.ToString("P1", CultureInfo.InvariantCulture);

    private static double? Rate(IEnumerable<PostMetric> metrics)
    {
        var list = metrics as IReadOnlyCollection<PostMetric> ?? metrics.ToList();
        var denominator = list.Sum(m => m.Impressions > 0 ? m.Impressions : m.Reach > 0 ? m.Reach : m.Views);
        return denominator == 0 ? null : (double)list.Sum(MetricsCalculator.Engagements) / denominator;
    }

    /// <summary>期間内に公開された投稿と、その最新の指標（1投稿1件）。</summary>
    private async Task<(Dictionary<Guid, PostVariant> Variants, List<PostMetric> Latest, Dictionary<Guid, MasterPost> Masters)> LoadAsync(
        AnalyticsFilter filter, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var query = db.PostVariants.Where(v => v.WorkspaceId == tenant.WorkspaceId && v.Status == VariantStatus.Published);
        if (filter.Platform is { } platform) query = query.Where(v => v.Platform == platform);
        if (filter.CampaignId is { } campaignId)
        {
            var postIds = db.MasterPosts.Where(p => p.CampaignId == campaignId).Select(p => p.Id);
            query = query.Where(v => postIds.Contains(v.MasterPostId));
        }
        var variants = await query.ToDictionaryAsync(v => v.Id, ct);
        var ids = variants.Keys.ToList();
        var latest = (await db.PostMetrics
                .Where(m => ids.Contains(m.PostVariantId) && m.PostedAt >= from && m.PostedAt < to)
                .ToListAsync(ct))
            .GroupBy(m => m.PostVariantId)
            .Select(g => g.MaxBy(m => m.CapturedAt)!)
            .ToList();
        var masterIds = latest.Select(m => variants[m.PostVariantId].MasterPostId).Distinct().ToList();
        var masters = await db.MasterPosts.Where(p => masterIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return (variants, latest, masters);
    }

    /// <summary>
    /// フォロワー純増（F-10 指標定義）＝ 期間末 − 期間初 のフォロワー数（チャネル合計）。
    /// 期間の前後に日次の記録がないチャネルは、期間内の最初と最後の記録で計算する。記録がなければ null。
    /// </summary>
    private async Task<double?> FollowerGrowthAsync(AnalyticsFilter filter, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var start = DateOnly.FromDateTime(from.UtcDateTime);
        var end = DateOnly.FromDateTime(to.UtcDateTime);
        var query = db.ChannelMetrics.Where(m => m.WorkspaceId == tenant.WorkspaceId && m.Date >= start.AddDays(-1) && m.Date <= end);
        if (filter.Platform is { } platform) query = query.Where(m => m.Platform == platform);
        var rows = await query.Select(m => new { m.ChannelId, m.Date, m.Followers }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        double total = 0;
        foreach (var g in rows.GroupBy(r => r.ChannelId))
        {
            var ordered = g.OrderBy(r => r.Date).ToList();
            if (ordered.Count >= 2) total += ordered[^1].Followers - ordered[0].Followers;
        }
        return total;
    }
}
