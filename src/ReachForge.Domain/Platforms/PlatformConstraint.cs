using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Platforms;

public enum ReleasePhase { Initial = 1, Phase2 = 2 }

/// <summary>リンクの扱い（F-06 変換ルール）。</summary>
public enum LinkPolicy
{
    /// <summary>本文に URL 可。</summary>
    Allowed,
    /// <summary>可能だが既定では含めない（X：URL 付き投稿は高額）。</summary>
    DiscouragedByCost,
    /// <summary>本文リンクが無効（Instagram）。「プロフィールのリンクから」へ誘導する。</summary>
    NotClickable,
    /// <summary>不可（TikTok）。</summary>
    NotAllowed,
    /// <summary>遷移先 URL 必須（Pinterest）。</summary>
    Required,
}

public sealed record AspectRatio(int Width, int Height)
{
    public double Value => (double)Width / Height;
    public override string ToString() => $"{Width}:{Height}";
}

/// <summary>
/// プラットフォーム制約マスタの1行（RF-DES-001 5.1）。生成・検証・配信の全工程で参照する。
/// 値は 2026年10月時点の調査値。運用管理画面（SCR-16）から上書きできる前提で、ここは初期値とする。
/// </summary>
public sealed record PlatformConstraint
{
    public required SocialPlatform Platform { get; init; }
    public required string DisplayName { get; init; }
    public required ReleasePhase Phase { get; init; }

    /// <summary>本文の最大文字数。</summary>
    public required int MaxBodyLength { get; init; }

    /// <summary>生成時の目安文字数（X は 140 字前後など）。</summary>
    public int? TargetBodyLength { get; init; }

    /// <summary>タイトルの最大文字数（YouTube / Pinterest）。</summary>
    public int? MaxTitleLength { get; init; }

    /// <summary>仕様上のハッシュタグ上限（超えるとエラー）。</summary>
    public int? MaxHashtags { get; init; }

    /// <summary>推奨ハッシュタグ数（範囲外は情報表示）。</summary>
    public required (int Min, int Max) RecommendedHashtags { get; init; }

    public required LinkPolicy LinkPolicy { get; init; }

    /// <summary>24時間あたりの投稿上限（API）。null は公開情報なし。</summary>
    public int? DailyPostLimit { get; init; }

    public required AspectRatio ImageAspect { get; init; }
    public required (int Width, int Height) ImageSize { get; init; }
    public AspectRatio? VideoAspect { get; init; }

    /// <summary>1投稿に添付できる画像の数（現状の実装範囲。Instagram・Threads のカルーセルは今後対応）。</summary>
    public int MaxImages { get; init; } = 1;

    /// <summary>動画しか投稿できない（YouTube）。</summary>
    public bool VideoOnly { get; init; }
    public int? MaxVideoSeconds { get; init; }

    /// <summary>「続きを読む」で切れる位置（Instagram 冒頭約125字）。プレビューで点線表示に使う。</summary>
    public int? FoldAt { get; init; }

    /// <summary>X の投稿単価（USD）。URL なし／URL あり。</summary>
    public decimal? CostPerPostUsd { get; init; }
    public decimal? CostPerPostWithUrlUsd { get; init; }

    /// <summary>生成 AI に渡す本文方針（F-06）。</summary>
    public required string StyleGuide { get; init; }

    /// <summary>注意事項（UI の制約1行表示などに使う）。</summary>
    public string? Note { get; init; }

    public string Summary =>
        MaxHashtags is { } h
            ? $"{DisplayName}：{MaxBodyLength:N0}字まで・ハッシュタグ{h}つまで"
            : $"{DisplayName}：{MaxBodyLength:N0}字まで";
}
