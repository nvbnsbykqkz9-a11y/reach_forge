using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Guardrails;

/// <summary>
/// ガードレールの指摘1件。UI では「理由と修正案をセットで」表示する（RF-UX-001 6.3）。
/// </summary>
public sealed record GuardrailFinding(
    GuardrailLevel Level,
    string Code,
    string Message,
    string? Reason = null,
    string? Excerpt = null,
    int? Index = null,
    string? Suggestion = null)
{
    /// <summary>修正案を適用した本文を返す（置換対象と修正案がある場合のみ）。</summary>
    public string Apply(string text) =>
        Excerpt is not null && Suggestion is not null ? text.Replace(Excerpt, Suggestion) : text;

    public bool CanAutoFix => Excerpt is not null && Suggestion is not null;
}

public sealed record GuardrailReport(IReadOnlyList<GuardrailFinding> Findings)
{
    public static readonly GuardrailReport Ok = new([]);

    public GuardrailLevel Level => Findings.Count == 0 ? GuardrailLevel.Ok : Findings.Max(f => f.Level);
    public bool HasErrors => Level == GuardrailLevel.Error;
    public int ErrorCount => Findings.Count(f => f.Level == GuardrailLevel.Error);
    public int WarningCount => Findings.Count(f => f.Level == GuardrailLevel.Warning);

    /// <summary>警告・エラーが0件（一括承認の対象条件、RF-UX-001 SCR-08）。</summary>
    public bool IsClean => Findings.All(f => f.Level <= GuardrailLevel.Info);

    public GuardrailReport Merge(GuardrailReport other) => new([.. Findings, .. other.Findings]);
}

/// <summary>ガードレール検査の入力。</summary>
public sealed record GuardrailContext
{
    public string Industry { get; init; } = "";
    public IReadOnlyCollection<string> NgWords { get; init; } = [];
    public IReadOnlyCollection<string> MustPhrases { get; init; } = [];
    public bool IsAdvertisement { get; init; }
    public IReadOnlyCollection<decimal> KnownPrices { get; init; } = [];
}
