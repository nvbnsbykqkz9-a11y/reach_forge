using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.Application.Ai;

/// <summary>投稿文生成の入力（F-03）。</summary>
public sealed record CopyRequest
{
    public required Guid WorkspaceId { get; init; }
    public required PostObjective Objective { get; init; }

    /// <summary>テーマ（必須、最大500字）。</summary>
    public required string Theme { get; init; }
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];
    public CopyFramework Framework { get; init; } = CopyFramework.Auto;
    public int Count { get; init; } = 3;
    public string Language { get; init; } = "ja";
    public IReadOnlyList<SocialPlatform> TargetPlatforms { get; init; } = [];
    public string? AdditionalInstructions { get; init; }
    public Guid? CampaignId { get; init; }

    public const int MaxThemeLength = 500;
    public const int MaxCount = 5;
}

/// <summary>構造化出力のスキーマ（RF-DES-001 付録 A.2）。</summary>
public sealed record GeneratedCopy(string Headline, string Body, string Cta, string[] Hashtags);

public sealed record CopyCandidate
{
    public required int Rank { get; init; }
    public required string Headline { get; init; }
    public required string Body { get; init; }
    public required string Cta { get; init; }
    public required IReadOnlyList<string> Hashtags { get; init; }

    /// <summary>ブランド適合スコア（1〜5）。</summary>
    public double BrandFitScore { get; init; }
    public string? BrandFitReason { get; init; }
    public required GuardrailReport Guardrail { get; init; }
}

public sealed record AiModelInfo(string Provider, string ModelId, bool FallbackUsed);

public sealed record CopyResult(Guid GenerationId, AiModelInfo Model, int CreditsUsed, IReadOnlyList<CopyCandidate> Candidates);

/// <summary>クイック修正チップ（RF-UX-001 6.1 原則4）。</summary>
public enum QuickFix
{
    Shorter,
    Longer,
    Casual,
    Polite,
    MoreEmoji,
    LessEmoji,
    StrongerCta,
}

public static class QuickFixLabels
{
    public static string ToLabel(this QuickFix q) => q switch
    {
        QuickFix.Shorter => "短く",
        QuickFix.Longer => "長く",
        QuickFix.Casual => "カジュアルに",
        QuickFix.Polite => "丁寧に",
        QuickFix.MoreEmoji => "絵文字を増やす",
        QuickFix.LessEmoji => "絵文字を減らす",
        QuickFix.StrongerCta => "CTAを強く",
        _ => q.ToString(),
    };

    public static string ToInstruction(this QuickFix q) => q switch
    {
        QuickFix.Shorter => "内容を保ったまま、本文を今の6割程度の長さに短くしてください。",
        QuickFix.Longer => "具体的な情景や理由を足して、本文を1.5倍程度に長くしてください。",
        QuickFix.Casual => "親しみやすいカジュアルな口調に書き換えてください。",
        QuickFix.Polite => "丁寧で落ち着いた口調に書き換えてください。",
        QuickFix.MoreEmoji => "文意に合う絵文字を2〜4個増やしてください。",
        QuickFix.LessEmoji => "絵文字を減らし、多くても1個にしてください。",
        QuickFix.StrongerCta => "行動を促す一文（CTA）をより具体的で強いものにしてください。",
        _ => "",
    };
}

/// <summary>投稿文生成（F-03）。</summary>
public interface ICopyGenerationService
{
    Task<CopyResult> GenerateAsync(CopyRequest request, CancellationToken ct);

    /// <summary>対象案のみの部分再生成（消費 1 クレジット）。</summary>
    Task<CopyResult> RefineAsync(CopyRequest request, GeneratedCopy candidate, QuickFix fix, CancellationToken ct);
}

public sealed record VariantRequest
{
    public required Guid WorkspaceId { get; init; }
    public required SocialPlatform Platform { get; init; }
    public required string Headline { get; init; }
    public required string Body { get; init; }
    public required string Cta { get; init; }
    public required IReadOnlyList<string> Hashtags { get; init; }
    public string? LinkUrl { get; init; }
    public string? CampaignCode { get; init; }
    public bool IncludeUrlForX { get; init; }
}

public sealed record GeneratedVariant(string Body, IReadOnlyList<string> Hashtags, string? Title, GuardrailReport Guardrail,
    AiModelInfo Model, int AutoFixAttempts);

/// <summary>マルチSNS最適化変換（F-06）。</summary>
public interface IVariantGenerationService
{
    Task<GeneratedVariant> GenerateAsync(VariantRequest request, Guid? generationId, CancellationToken ct);
}

/// <summary>承認者向け「確認ポイント要約」（F-07-1）。</summary>
public interface IApprovalSummaryService
{
    Task<IReadOnlyList<string>> SummarizeAsync(Guid variantId, CancellationToken ct);
}

/// <summary>AI 呼び出しの失敗（全プロバイダ障害・安全性ブロック）。</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null)
    : Domain.Common.DomainException(Domain.Common.ErrorCodes.AiUnavailable, message)
{
    public Exception? Inner { get; } = inner;
}

public sealed class AiSafetyBlockedException(string category)
    : Domain.Common.DomainException(Domain.Common.ErrorCodes.AiSafetyBlocked,
        $"安全性ポリシーにより生成できませんでした（分類：{category}）。表現を変えてお試しください。")
{
    public string Category { get; } = category;
}

/// <summary>広告文の依頼（SNS・目的・伝えたいこと）。</summary>
public sealed record AdCopyRequest(SocialPlatform Platform, AdObjective Objective, string Theme, string? LinkUrl);

/// <summary>広告文の案（本文・見出し・説明・ボタン）。</summary>
public sealed record AdCopyCandidate(string PrimaryText, string Headline, string Description, string CallToAction,
    Domain.Guardrails.GuardrailReport? Guardrail = null);

/// <summary>広告文の長さの上限（各社の入力欄の上限・推奨）。</summary>
public sealed record AdCopyLimits(int PrimaryText, int Headline, int Description)
{
    public static AdCopyLimits For(SocialPlatform platform) => platform switch
    {
        SocialPlatform.TikTok => new(100, 0, 0),
        SocialPlatform.X => new(280, 70, 0),
        SocialPlatform.YouTube => new(90, 40, 90),
        _ => new(125, 40, 30),
    };
}

/// <summary>広告文をつくる（LLM の構造化出力）。景表法・薬機法の規制表現と、ブランドの NG ワードは使わない。</summary>
public interface IAdCopyWriter
{
    Task<IReadOnlyList<AdCopyCandidate>> WriteAsync(Services.BrandContext brand, AdCopyRequest request, CancellationToken ct);
}
