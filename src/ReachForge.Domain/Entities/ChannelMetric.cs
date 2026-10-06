using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>アカウント単位の日次指標（フォロワー数など。フォロワー純増＝期間末 − 期間初）。</summary>
public sealed class ChannelMetric : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ChannelId { get; set; }
    public SocialPlatform Platform { get; set; }

    /// <summary>集計日（テナントのタイムゾーンではなく UTC の日付）。</summary>
    public DateOnly Date { get; set; }
    public long Followers { get; set; }
    public long Impressions { get; set; }
    public long ProfileVisits { get; set; }
}
