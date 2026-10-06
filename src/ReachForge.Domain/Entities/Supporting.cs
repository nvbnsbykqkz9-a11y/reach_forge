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

/// <summary>監査ログ（ログイン、権限変更、投稿公開、AI 設定変更、トークン操作。2年保存）。</summary>
public sealed class AuditLog : Entity
{
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string? Detail { get; set; }
}
