using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Engagement;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Services;

/// <summary>イベント辞書（記念日・行事）からの候補（今日から45日以内）。</summary>
public sealed class EventCalendarTrendSource : ITrendSource
{
    public Task<IReadOnlyList<TrendCandidate>> CollectAsync(DateOnly today, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrendCandidate>>(SeasonalEvents.Upcoming(today, 45)
            .Select(e => new TrendCandidate(e.Name, e.Date, TrendSource.EventCalendar)).ToList());
}

/// <summary>
/// ネタ帳（F-12）：候補を集め → 不謹慎な話題を除外 → AI がブランドとの関連度で採点 → 上位10件を保存（毎日 06:00 JST）。
/// 競合の投稿本文の転載・模倣は行わない（傾向の要約のみ。競合アカウントの取得は従量課金の検索 API 導入時に追加）。
/// </summary>
public sealed class TrendService(
    IAppDbContext db,
    ITenantContext tenant,
    IEnumerable<ITrendSource> sources,
    ITrendIdeaWriter writer,
    IBrandContextProvider brand,
    SchedulingService scheduling,
    TimeProvider clock,
    ILogger<TrendService> log)
{
    public const int KeepTop = 10;
    public const double MinRelevance = 0.3;

    /// <summary>このワークスペースのネタ帳を更新する（画面の「更新」・毎日のジョブ）。</summary>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        return await RefreshWorkspaceAsync(tenant.TenantId, tenant.WorkspaceId, await scheduling.TenantTimeZoneAsync(ct), ct);
    }

    /// <summary>TrendResearchJob（14章：毎日 06:00 JST）。その日にまだ更新していないワークスペースを更新する。</summary>
    public async Task<int> RefreshDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("RefreshDueAsync はシステムコンテキストで実行してください。");
        var tenants = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var workspaces = await db.Workspaces.AsNoTracking().ToListAsync(ct);
        var count = 0;
        foreach (var w in workspaces)
        {
            if (!tenants.TryGetValue(w.TenantId, out var t)) continue;
            var tz = SchedulingService.FindTimeZone(t.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);
            if (local.Hour < 6) continue;
            var today = DateOnly.FromDateTime(local.Date);
            if (await db.TrendIdeas.AnyAsync(i => i.WorkspaceId == w.Id && i.GeneratedOn == today, ct)) continue;
            try
            {
                count += await RefreshWorkspaceAsync(w.TenantId, w.Id, tz, ct);
            }
            catch (DomainException ex)
            {
                log.LogWarning(ex, "Trend refresh failed for workspace {WorkspaceId}", w.Id);
            }
        }
        return count;
    }

    private async Task<int> RefreshWorkspaceAsync(Guid tenantId, Guid workspaceId, TimeZoneInfo tz, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).Date);
        var candidates = new List<TrendCandidate>();
        foreach (var source in sources) candidates.AddRange(await source.CollectAsync(today, ct));
        candidates = candidates.Where(c => !SensitiveTopicFilter.IsSensitive($"{c.Topic} {c.Snippet}"))
            .DistinctBy(c => c.Topic).Take(30).ToList();

        // 使った・不要にした話題はもう出さない
        var existing = await db.TrendIdeas.Where(i => i.WorkspaceId == workspaceId).ToListAsync(ct);
        var done = existing.Where(i => i.Status != IdeaStatus.New).Select(i => i.Topic).ToHashSet();
        candidates = candidates.Where(c => !done.Contains(c.Topic)).ToList();

        var ctx = await brand.BuildAsync(workspaceId, [], null, ct);
        var scored = await writer.ScoreAsync(ctx, candidates, ct);
        var top = scored.Where(s => !s.Sensitive && s.Relevance >= MinRelevance && !SensitiveTopicFilter.IsSensitive(s.Topic))
            .OrderByDescending(s => s.Relevance).Take(KeepTop).ToList();

        // 未使用の古いネタは入れ替える
        db.TrendIdeas.RemoveRange(existing.Where(i => i.Status == IdeaStatus.New));
        foreach (var idea in top)
        {
            var c = candidates.First(x => x.Topic == idea.Topic);
            var date = c.Date is { } d ? d.AddDays(-idea.DaysBefore) : today.AddDays(1);
            if (date < today) date = today;
            db.TrendIdeas.Add(new TrendIdea
            {
                TenantId = tenantId, WorkspaceId = workspaceId, Topic = idea.Topic, Source = c.Source, RecommendedDate = date,
                Format = idea.Format, Relevance = idea.Relevance, Angles = [.. idea.Angles], Reason = idea.Reason, GeneratedOn = today,
            });
        }
        await db.SaveChangesAsync(ct);
        return top.Count;
    }

    public async Task<IReadOnlyList<TrendIdea>> ListAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewBrand);
        return (await db.TrendIdeas.AsNoTracking().Where(i => i.WorkspaceId == tenant.WorkspaceId && i.Status == IdeaStatus.New).ToListAsync(ct))
            .OrderBy(i => i.RecommendedDate).ThenByDescending(i => i.Relevance).ToList();
    }

    public async Task SetStatusAsync(Guid id, IdeaStatus status, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var idea = await db.TrendIdeas.FirstOrDefaultAsync(i => i.Id == id && i.WorkspaceId == tenant.WorkspaceId, ct)
                   ?? throw new NotFoundException("ネタ");
        idea.Status = status;
        await db.SaveChangesAsync(ct);
    }
}
