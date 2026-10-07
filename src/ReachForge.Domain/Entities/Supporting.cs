using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>承認・差戻し・コメントの履歴（監査ログにも記録する）。</summary>
public sealed class ApprovalAction : Entity
{
    public Guid PostVariantId { get; set; }
    public ApprovalDecision Decision { get; set; }
    public required string ActorName { get; set; }
    public string? Comment { get; set; }
}

/// <summary>AI 生成の記録（来歴）。ユーザー操作1回＝1件。</summary>
public sealed class AiGeneration : Entity
{
    public Guid WorkspaceId { get; set; }
    public AiTaskType TaskType { get; set; }
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string PromptKey { get; set; } = "";
    public int PromptVersion { get; set; }
    public int BrandProfileVersion { get; set; }

    /// <summary>入力（個人情報マスク後）の JSON。</summary>
    public string InputSnapshot { get; set; } = "{}";
    public string Output { get; set; } = "";
    public AiGenerationStatus Status { get; set; } = AiGenerationStatus.Queued;
    public string? ErrorCode { get; set; }
    public int Credits { get; set; }
    public int LatencyMs { get; set; }
    public bool FallbackUsed { get; set; }
}

/// <summary>AI 呼び出し1回ごとの計量（RF-DES-001 4.7）。</summary>
public sealed class AiUsageLog : Entity
{
    public Guid? AiGenerationId { get; set; }
    public AiTaskType TaskType { get; set; }
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Images { get; set; }
    public int VideoSeconds { get; set; }
    public decimal CostUsd { get; set; }
    public int LatencyMs { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Succeeded { get; set; }
}

/// <summary>投稿の時系列指標（公開後 1h, 6h, 24h, 72h, 7d, 30d で取得）。</summary>
public sealed class PostMetric : Entity
{
    public Guid PostVariantId { get; set; }
    public SocialPlatform Platform { get; set; }
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>投稿の公開日時（最適時刻の集計に使う）。</summary>
    public DateTimeOffset PostedAt { get; set; }
    public long Impressions { get; set; }
    public long Reach { get; set; }
    public long Views { get; set; }
    public int Likes { get; set; }
    public int Comments { get; set; }
    public int Shares { get; set; }
    public int Saves { get; set; }
    public int LinkClicks { get; set; }
    public int ProfileVisits { get; set; }
    public int Follows { get; set; }
    public int Conversions { get; set; }
}

/// <summary>
/// 投稿指標の月次集計（RF-DES-001 6章：post_metric は13か月超を集計テーブルへ移して明細を削除する）。
/// 投稿・月ごとに、その月の最後のスナップショットの値を持つ（指標は累計値のため、最後の値がその時点の成果）。
/// </summary>
public sealed class PostMetricRollup : Entity
{
    public Guid PostVariantId { get; set; }
    public SocialPlatform Platform { get; set; }

    /// <summary>集計した月（取得日時の月初、UTC）。</summary>
    public DateOnly Month { get; set; }
    public DateTimeOffset PostedAt { get; set; }

    /// <summary>その月の最後のスナップショットの取得日時。</summary>
    public DateTimeOffset LastCapturedAt { get; set; }
    public int Snapshots { get; set; }
    public long Impressions { get; set; }
    public long Reach { get; set; }
    public long Views { get; set; }
    public int Likes { get; set; }
    public int Comments { get; set; }
    public int Shares { get; set; }
    public int Saves { get; set; }
    public int LinkClicks { get; set; }
    public int ProfileVisits { get; set; }
    public int Follows { get; set; }
    public int Conversions { get; set; }

    /// <summary>分析で明細と同じように扱うためのスナップショット。</summary>
    public PostMetric ToSnapshot() => new()
    {
        TenantId = TenantId, PostVariantId = PostVariantId, Platform = Platform, CapturedAt = LastCapturedAt, PostedAt = PostedAt,
        Impressions = Impressions, Reach = Reach, Views = Views, Likes = Likes, Comments = Comments, Shares = Shares, Saves = Saves,
        LinkClicks = LinkClicks, ProfileVisits = ProfileVisits, Follows = Follows, Conversions = Conversions,
    };

    public void Absorb(PostMetric m, int count)
    {
        Snapshots += count;
        if (m.CapturedAt < LastCapturedAt) return;
        LastCapturedAt = m.CapturedAt;
        PostedAt = m.PostedAt;
        Impressions = m.Impressions; Reach = m.Reach; Views = m.Views; Likes = m.Likes; Comments = m.Comments; Shares = m.Shares;
        Saves = m.Saves; LinkClicks = m.LinkClicks; ProfileVisits = m.ProfileVisits; Follows = m.Follows; Conversions = m.Conversions;
    }
}

/// <summary>監査ログ（ログイン、権限変更、投稿公開、AI 設定変更、トークン操作。2年保存）。</summary>
public sealed class AuditLog : Entity
{
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string? Detail { get; set; }
}
