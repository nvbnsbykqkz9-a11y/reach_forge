using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>キャンペーンの目標にする指標（F-11：期間・目的・予算・KPI）。</summary>
public enum CampaignKpi : short { Impressions = 1, EngagementRate = 2, LinkClicks = 3, Followers = 4 }

/// <summary>キャンペーン（F-11）。広告扱いの場合は PR 表記を必須にする（ステマ規制）。</summary>
public sealed class Campaign : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>UTM の utm_campaign に使うコード。</summary>
    public required string Code { get; set; }

    public PostObjective Objective { get; set; } = PostObjective.Awareness;
    public bool IsAdvertisement { get; set; }
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }

    /// <summary>予算（円、任意）。AI・SNS 費用の目安と比べる。</summary>
    public decimal? Budget { get; set; }
    public CampaignKpi Kpi { get; set; } = CampaignKpi.Impressions;

    /// <summary>KPI の目標値（反応の割合は 0.05 = 5%）。</summary>
    public double? KpiTarget { get; set; }
    public List<SocialPlatform> Platforms { get; set; } = [];
    public string Description { get; set; } = "";

    public bool IsActive(DateOnly today) => (StartsOn is null || StartsOn <= today) && (EndsOn is null || today <= EndsOn);
}

/// <summary>A/B テストで変える要素（F-11-1：1つだけ指定する）。</summary>
public enum AbVariable : short { Hook = 1, Image = 2, Cta = 3, TimeSlot = 4 }

/// <summary>
/// 配信方法（F-11-2）。SNS の仕様上、同時に母集団を分けられないため、同じチャネルで日時をずらす（曜日・時刻をそろえて1週間後）か、
/// 異なるチャネル間で同時に比べる。
/// </summary>
public enum AbMode : short { SameChannelStaggered = 1, CrossChannel = 2 }

public enum AbTestStatus : short { Draft = 1, Running = 2, Completed = 3, Canceled = 4 }

public sealed class AbTest : Entity
{
    /// <summary>判定は公開から72時間後の反応で行う。</summary>
    public static readonly TimeSpan EvaluateAfter = TimeSpan.FromHours(72);

    /// <summary>判定保留のまま待つ上限（公開から14日）。</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(14);

    public Guid WorkspaceId { get; set; }
    public Guid? CampaignId { get; set; }
    public required string Name { get; set; }
    public AbVariable Variable { get; set; }
    public AbMode Mode { get; set; } = AbMode.SameChannelStaggered;
    public Guid VariantAId { get; set; }
    public Guid VariantBId { get; set; }
    public AbTestStatus Status { get; set; } = AbTestStatus.Draft;
    public DateTimeOffset? StartAt { get; set; }

    // ---- 判定結果（システムが計算） ----
    public string? Verdict { get; set; }
    public double? RateA { get; set; }
    public double? RateB { get; set; }
    public double? PValue { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? EvaluatedAt { get; set; }
    public bool WinnerRegistered { get; set; }
    public string CreatedBy { get; set; } = "";
}
