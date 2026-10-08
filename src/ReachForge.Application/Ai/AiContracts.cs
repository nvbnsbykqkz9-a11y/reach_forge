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

public sealed record CopyResult(Guid GenerationId, AiModelInfo Model, IReadOnlyList<CopyCandidate> Candidates);

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

    /// <summary>対象案のみの部分再生成。</summary>
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

/// <summary>広告文の長さの上限（各社の入力欄の上限・推奨）。</summary>
public sealed record AdCopyLimits(int PrimaryText, int Headline, int Description)
{
    public static AdCopyLimits For(SocialPlatform platform) => platform switch
    {
        SocialPlatform.TikTok => new(100, 0, 0),
        SocialPlatform.X => new(280, 70, 0),
        SocialPlatform.YouTube => new(90, 40, 90),
        SocialPlatform.Line => new(75, 20, 0),
        _ => new(125, 40, 30),
    };
}

/// <summary>LP からつくった、1つの SNS 向けの文章（広告文の案3つ・投稿文・ハッシュタグ）とガードレールの結果。</summary>
public sealed record LpCreative(IReadOnlyList<LpAdCopy> AdCopies, string PostText, IReadOnlyList<string> Hashtags,
    Domain.Guardrails.GuardrailReport Guardrail);

/// <summary>
/// LP の内容から、SNS ごとの広告文と投稿文をつくる（LLM の構造化出力）。数値・効果は LP に書かれたものだけを使い、
/// 景表法・薬機法の規制表現とブランドの NG ワードは使わない。各社の文字数の上限に収める。
/// </summary>
public interface ILpCreativeWriter
{
    Task<LpCreative> WriteAsync(Services.BrandContext brand, WebPage page, SocialPlatform platform, CancellationToken ct);
}

/// <summary>LP の画像の候補（AI に見せる縮小画像と、元の大きさ・代替テキスト）。</summary>
public sealed record LpImageCandidate(int Index, byte[] Thumbnail, string ThumbnailMime, int Width, int Height, string? Alt);

/// <summary>AI が選んだ LP の画像。<paramref name="Description"/> は何が写っているか（日本語）。</summary>
public sealed record LpImagePick(int Index, string Description);

/// <summary>
/// LP の画像から、商品・サービスの特色が伝わる画像を選ぶ（画像理解モデル）。ロゴ・アイコン・画面のスクリーンショット・
/// 文字だけのバナーは選ばない。おすすめの順に返す。
/// </summary>
public interface ILpImageCurator
{
    Task<IReadOnlyList<LpImagePick>> PickAsync(WebPage page, IReadOnlyList<LpImageCandidate> candidates, int max, CancellationToken ct);
}

/// <summary>広告のビジュアル案（元にする画像・切り口・画像に入れる見出し・画像生成と動画生成への指示）。</summary>
public sealed record LpVisualConcept(int SourceIndex, string Angle, string Headline, string ImagePrompt, string MotionPrompt);

/// <summary>
/// LP の内容と選んだ画像から、広告のビジュアル案をつくる（画像ごとに1案）。見出しは NG 語・規制表現・LP にない価格を確認する。
/// </summary>
public interface ILpVisualPlanner
{
    Task<IReadOnlyList<LpVisualConcept>> PlanAsync(Services.BrandContext brand, WebPage page, IReadOnlyList<string> sourceDescriptions,
        CancellationToken ct);
}
