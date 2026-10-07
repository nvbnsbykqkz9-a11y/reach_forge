using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

public enum PromptStatus : short
{
    /// <summary>編集中。生成には使わない。</summary>
    Draft = 1,

    /// <summary>全テナントで使う版（キーごとに1つ）。</summary>
    Active = 2,

    /// <summary>一部のテナントだけで試す版（<see cref="PromptTemplate.RolloutPercent"/>%、RF-DES-001 10.3 段階適用）。</summary>
    Candidate = 3,

    Archived = 4,
}

/// <summary>
/// プロンプトテンプレート（RF-DES-001 4.5）。Scriban 形式で、版数・作成者・評価スコアを持つ。
/// テナントに属さないシステム全体の設定（TenantId は空）で、運用者だけが編集する。
/// </summary>
public sealed class PromptTemplate : Entity
{
    public const int MaxBodyLength = 20_000;

    public required string Key { get; set; }
    public int Version { get; set; }
    public required string Body { get; set; }
    public PromptStatus Status { get; set; } = PromptStatus.Draft;

    /// <summary>Candidate のとき、この版を使うテナントの割合（0〜100）。</summary>
    public int RolloutPercent { get; set; }

    public string Note { get; set; } = "";
    public string CreatedBy { get; set; } = "";

    /// <summary>直近の評価（AI Evals）。未評価なら null。</summary>
    public PromptEvalResult? Evaluation { get; set; }
}

/// <summary>評価結果の要約（RF-DES-001 4.6 の評価軸）。</summary>
public sealed record PromptEvalResult(
    DateTimeOffset EvaluatedAt,
    string Model,
    int Cases,
    double? BrandFit,
    double ConstraintRate,
    int SafetyViolations,
    int FactMismatches,
    bool Passed,
    string Summary);
