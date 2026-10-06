using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

public enum ReportKind : short { Weekly = 1, Monthly = 2, Custom = 3 }

public enum ReportStatus : short { Queued = 1, Running = 2, Succeeded = 3, Failed = 4 }

/// <summary>
/// AI レポート（F-10）。数値はシステムが確定した集計（DataJson）、文章は AI の考察（InsightJson）。
/// 考察の各主張は根拠の数値（ファクト ID）を引用させ、数値の整合を事後検証してから保存する。
/// </summary>
public sealed class Report : Entity
{
    public Guid WorkspaceId { get; set; }
    public ReportKind Kind { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset PeriodFrom { get; set; }
    public DateTimeOffset PeriodTo { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Queued;

    /// <summary>集計結果（KPI・SNS別・推移・上位下位投稿・根拠ファクト）の JSON。</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>AI の考察（検証済みの主張のみ）の JSON。</summary>
    public string InsightJson { get; set; } = "{}";

    /// <summary>検証で除外した主張の数（根拠のない数値・存在しないファクトの引用）。</summary>
    public int RejectedClaims { get; set; }

    /// <summary>PDF の保存先（メディアストレージのパス）。</summary>
    public string? PdfPath { get; set; }
    public Guid? AiJobId { get; set; }
    public string ModelId { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>レポートの定期作成・配信の設定（ワークスペース単位、RF-UX-001 SCR-10）。</summary>
public sealed class ReportSettings
{
    /// <summary>毎週月曜 07:00（テナントのタイムゾーン）に先週分を作成する。</summary>
    public bool Weekly { get; set; }

    /// <summary>毎月1日 07:00 に先月分を作成する。</summary>
    public bool Monthly { get; set; }

    /// <summary>完成したレポートを送るメールアドレス。</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>PDF の表紙に載せるロゴ（クライアント向けに差し替え可能）。未設定ならブランドのロゴ。</summary>
    public Guid? LogoAssetId { get; set; }
}
