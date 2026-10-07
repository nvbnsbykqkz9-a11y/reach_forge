using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

/// <summary>料金の内訳1行（機能別・サービス別）。</summary>
public sealed record AiCostLine(string Label, decimal Usd, int Calls);

/// <summary>1か月分の AI の利用料金（目安）。</summary>
public sealed record AiCostMonth(DateOnly Month, decimal TotalUsd, int Calls, IReadOnlyList<AiCostLine> ByFeature, IReadOnlyList<AiCostLine> ByService);

public sealed record AiCostSummary(AiCostMonth ThisMonth, AiCostMonth LastMonth);

/// <summary>
/// AI の利用料金の目安。AI を呼ぶたびに記録した推定料金（設定の単価 × トークン数・画像の枚数・動画の秒数）を月ごとに集計する。
/// 実際の請求は、各 AI サービスの管理画面で確認する（為替・税・無料枠・単価の改定は反映しない）。
/// </summary>
public sealed class AiCostService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<AiCostSummary> SummaryAsync(CancellationToken ct)
    {
        var zoneId = await db.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.TimeZoneId).FirstOrDefaultAsync(ct);
        var tz = TimeZones.Find(zoneId);
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);
        var thisMonth = new DateOnly(local.Year, local.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var since = Start(lastMonth, tz);
        var boundary = Start(thisMonth, tz);

        var rows = await db.AiUsageLogs.AsNoTracking()
            .Where(l => l.CreatedAt >= since)
            .Select(l => new { l.CreatedAt, l.TaskType, l.Provider, l.CostUsd })
            .ToListAsync(ct);
        return new AiCostSummary(
            Month(thisMonth, rows.Where(r => r.CreatedAt >= boundary).Select(r => (r.TaskType, r.Provider, r.CostUsd))),
            Month(lastMonth, rows.Where(r => r.CreatedAt < boundary).Select(r => (r.TaskType, r.Provider, r.CostUsd))));
    }

    private static DateTimeOffset Start(DateOnly month, TimeZoneInfo tz)
    {
        var local = month.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static AiCostMonth Month(DateOnly month, IEnumerable<(AiTaskType Task, string Provider, decimal Usd)> rows)
    {
        var list = rows.ToList();
        return new AiCostMonth(month, list.Sum(r => r.Usd), list.Count,
            Lines(list.GroupBy(r => FeatureLabel(r.Task))),
            Lines(list.GroupBy(r => ServiceLabel(r.Provider))));

        static List<AiCostLine> Lines(IEnumerable<IGrouping<string, (AiTaskType, string, decimal Usd)>> groups) =>
            [.. groups.Select(g => new AiCostLine(g.Key, g.Sum(x => x.Usd), g.Count())).OrderByDescending(l => l.Usd).ThenByDescending(l => l.Calls)];
    }

    public static string FeatureLabel(AiTaskType t) => t switch
    {
        AiTaskType.Copy => "広告文・投稿文",
        AiTaskType.Judge => "文章の品質チェック",
        AiTaskType.Vision => "画像の説明文（ALT）",
        AiTaskType.Image => "画像の生成",
        AiTaskType.ImageEdit => "画像の編集",
        AiTaskType.Video => "動画の構成",
        AiTaskType.VideoGeneration => "動画の生成（AI の映像）",
        AiTaskType.Tts => "ナレーション",
        _ => "その他",
    };

    public static string ServiceLabel(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => "Anthropic（Claude）",
        "openai" => "OpenAI",
        "google" => "Google",
        "local" or "stub" or "" => "お試し（料金なし）",
        _ => provider,
    };
}
