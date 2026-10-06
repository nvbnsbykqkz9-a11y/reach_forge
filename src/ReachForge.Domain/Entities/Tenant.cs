using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

/// <summary>契約単位（企業・店舗・代理店）。データはテナント単位で論理分離する。</summary>
public sealed class Tenant : Entity
{
    public required string Name { get; set; }

    /// <summary>テナントのタイムゾーン（IANA）。予約時刻の入力・表示に使う。保存は UTC。</summary>
    public string TimeZoneId { get; set; } = "Asia/Tokyo";

    /// <summary>プランの連携可能チャネル数上限（F-01）。</summary>
    public int MaxChannels { get; set; } = 10;

    /// <summary>テナント単位の全予約一時停止（F-08 緊急停止）。</summary>
    public bool PublishingPaused { get; set; }

    public Tenant() => TenantId = Id;
}
