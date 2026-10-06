using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record SaveCampaign
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public PostObjective Objective { get; init; } = PostObjective.Awareness;
    public bool IsAdvertisement { get; init; }
    public DateOnly? StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public decimal? Budget { get; init; }
    public CampaignKpi Kpi { get; init; } = CampaignKpi.Impressions;
    public double? KpiTarget { get; init; }
    public IReadOnlyList<SocialPlatform> Platforms { get; init; } = [];
    public string Description { get; init; } = "";
}

/// <summary>キャンペーンの進み具合（KPI の実績はシステムが計算）。</summary>
public sealed record CampaignSummary(Campaign Campaign, int Posts, int Published, double KpiActual, double? Progress, int AbTests);

/// <summary>キャンペーン（F-11）：期間・目的・予算・KPI を持ち、投稿を束ねる。UTM の utm_campaign にコードを使う。</summary>
public sealed partial class CampaignService(IAppDbContext db, ITenantContext tenant, AnalyticsService analytics, SchedulingService scheduling,
    TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,39}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<CampaignSummary>> ListAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var campaigns = await db.Campaigns.AsNoTracking().Where(c => c.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct);
        var result = new List<CampaignSummary>();
        foreach (var c in campaigns.OrderByDescending(c => c.StartsOn ?? DateOnly.MinValue).ThenBy(c => c.Name))
        {
            result.Add(await SummarizeAsync(c, ct));
        }
        return result;
    }

    public async Task<CampaignSummary> GetAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == tenant.WorkspaceId, ct)
                ?? throw new NotFoundException("キャンペーン");
        return await SummarizeAsync(c, ct);
    }

    public async Task<Campaign> SaveAsync(SaveCampaign cmd, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var name = cmd.Name.Trim();
        var code = cmd.Code.Trim().ToLowerInvariant();
        if (name.Length is 0 or > 100) throw new DomainException(ErrorCodes.Validation, "キャンペーン名を100字以内で入力してください。");
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(ErrorCodes.Validation, "コードは半角英小文字・数字・ハイフンで2〜40字にしてください（例：autumn2026）。");
        }
        if (cmd.StartsOn is { } s && cmd.EndsOn is { } e && e < s)
        {
            throw new DomainException(ErrorCodes.Validation, "終了日は開始日以降にしてください。");
        }
        if (cmd.Budget is < 0 || cmd.KpiTarget is < 0) throw new DomainException(ErrorCodes.Validation, "予算・目標値は0以上で入力してください。");
        if (cmd.Kpi == CampaignKpi.EngagementRate && cmd.KpiTarget is > 1)
        {
            throw new DomainException(ErrorCodes.Validation, "反応の割合の目標は 0〜100% で入力してください。");
        }
        if (await db.Campaigns.AnyAsync(c => c.WorkspaceId == tenant.WorkspaceId && c.Code == code && c.Id != cmd.Id, ct))
        {
            throw new DomainException(ErrorCodes.Validation, "このコードはほかのキャンペーンで使われています。");
        }

        Campaign campaign;
        if (cmd.Id is { } id)
        {
            campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == tenant.WorkspaceId, ct)
                       ?? throw new NotFoundException("キャンペーン");
            campaign.Name = name;
            campaign.Code = code;
        }
        else
        {
            campaign = new Campaign { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Name = name, Code = code };
            db.Campaigns.Add(campaign);
        }
        campaign.Objective = cmd.Objective;
        campaign.IsAdvertisement = cmd.IsAdvertisement;
        campaign.StartsOn = cmd.StartsOn;
        campaign.EndsOn = cmd.EndsOn;
        campaign.Budget = cmd.Budget;
        campaign.Kpi = cmd.Kpi;
        campaign.KpiTarget = cmd.KpiTarget;
        campaign.Platforms = [.. cmd.Platforms.Distinct()];
        campaign.Description = cmd.Description.Trim();
        db.Record(tenant, cmd.Id is null ? "campaign.created" : "campaign.updated", nameof(Campaign), campaign.Id);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    /// <summary>キャンペーンの投稿（SNS 別）。A/B テストの A 案を選ぶのに使う。</summary>
    public async Task<IReadOnlyList<(PostVariant Variant, string Title, string ChannelName)>> PostsAsync(Guid campaignId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var posts = await db.MasterPosts.AsNoTracking().Where(p => p.CampaignId == campaignId && p.WorkspaceId == tenant.WorkspaceId)
            .ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        var ids = posts.Keys.ToList();
        var variants = await db.PostVariants.AsNoTracking().Where(v => ids.Contains(v.MasterPostId)).ToListAsync(ct);
        var channels = await db.Channels.AsNoTracking().Where(c => c.WorkspaceId == tenant.WorkspaceId).ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
        return variants.OrderByDescending(v => v.CreatedAt)
            .Select(v => (v, posts[v.MasterPostId], channels.GetValueOrDefault(v.ChannelId, "")))
            .ToList();
    }

    /// <summary>投稿が1件もないキャンペーンだけ削除できる。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == tenant.WorkspaceId, ct)
                ?? throw new NotFoundException("キャンペーン");
        if (await db.MasterPosts.AnyAsync(p => p.CampaignId == id, ct))
        {
            throw new DomainException(ErrorCodes.Validation, "投稿があるキャンペーンは削除できません。終了日を設定してください。");
        }
        db.Campaigns.Remove(c);
        db.Record(tenant, "campaign.deleted", nameof(Campaign), id);
        await db.SaveChangesAsync(ct);
    }

    private async Task<CampaignSummary> SummarizeAsync(Campaign c, CancellationToken ct)
    {
        var postIds = db.MasterPosts.Where(p => p.CampaignId == c.Id).Select(p => p.Id);
        var variants = await db.PostVariants.Where(v => postIds.Contains(v.MasterPostId)).Select(v => v.Status).ToListAsync(ct);
        var tz = await scheduling.TenantTimeZoneAsync(ct);
        var now = clock.GetUtcNow();
        var from = c.StartsOn is { } s ? Local(s, tz) : now.AddDays(-90);
        var to = c.EndsOn is { } e ? Local(e.AddDays(1), tz) : now;
        if (to > now) to = now;
        double actual = 0;
        if (to > from)
        {
            var kpis = await analytics.KpisAsync(new AnalyticsFilter(from, to, CampaignId: c.Id), ct);
            actual = c.Kpi switch
            {
                CampaignKpi.Impressions => kpis[0].Value,
                CampaignKpi.EngagementRate => kpis[1].Value,
                CampaignKpi.LinkClicks => kpis[2].Value,
                _ => kpis[3].Value,
            };
        }
        var abTests = await db.AbTests.CountAsync(t => t.CampaignId == c.Id, ct);
        return new CampaignSummary(c, variants.Count, variants.Count(v => v == VariantStatus.Published), actual,
            c.KpiTarget is > 0 ? actual / c.KpiTarget.Value : null, abTests);
    }

    private static DateTimeOffset Local(DateOnly date, TimeZoneInfo tz)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }
}
