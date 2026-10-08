using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Domain.Entities;

/// <summary>広告文の案（本文・見出し・説明・ボタン）。</summary>
public sealed record LpAdCopy(string PrimaryText, string Headline, string Description, string CallToAction);

/// <summary>SNS の形式に合わせて書き出した画像・動画（形式の識別子・用途・大きさと、ファイル）。</summary>
public sealed record LpMedia(string Key, string Label, int Width, int Height, Guid AssetId)
{
    public string Description => $"{Label}（{Width}×{Height}）";
}

/// <summary>1つの SNS 向けにつくったもの（広告文の案・投稿文・ハッシュタグ・SNS の形式ごとの画像と動画）。</summary>
public sealed record LpPlatformOutput
{
    public IReadOnlyList<LpAdCopy> AdCopies { get; init; } = [];
    public string PostText { get; init; } = "";
    public IReadOnlyList<string> Hashtags { get; init; } = [];

    /// <summary>以前の形式（LP の画像を SNS の大きさに切り出しただけのもの）。新しくつくったものでは空。</summary>
    public IReadOnlyList<Guid> ImageAssetIds { get; init; } = [];

    /// <summary>SNS の形式ごとの広告画像（AI のキービジュアルに見出しを入れたもの）。</summary>
    public IReadOnlyList<LpMedia> Images { get; init; } = [];

    /// <summary>SNS の形式ごとの動画（縦型・横型のうち、その SNS で使うもの）。</summary>
    public IReadOnlyList<LpMedia> Videos { get; init; } = [];
}

/// <summary>LP から選んだ画像（AI が「特色がある」と選んだもの）と、何が写っているか。</summary>
public sealed record LpSourceImage(Guid AssetId, string Url, string Description);

/// <summary>
/// 広告のビジュアル案：元にする LP の画像・訴求の切り口・画像に入れる見出しと、向きごとの AI のキービジュアル。
/// </summary>
public sealed record LpVisual
{
    public int SourceIndex { get; init; }
    public string Angle { get; init; } = "";
    public string Headline { get; init; } = "";
    public string ImagePrompt { get; init; } = "";
    public string MotionPrompt { get; init; } = "";
    public Dictionary<MediaOrientation, Guid> KeyVisuals { get; init; } = [];

    /// <summary>AI でつくれず、LP の画像をそのまま使った（API キーがないなど）。</summary>
    public bool Fallback { get; init; }
}

public enum LpProjectStatus : short
{
    /// <summary>文章・画像をつくっている。</summary>
    Creating = 1,
    /// <summary>文章ができた（画像・動画はジョブで続けてつくる）。</summary>
    Ready = 2,
    Failed = 3,
}

/// <summary>
/// LP（ランディングページ）から、選んだ SNS 向けの広告文・投稿文・画像と縦型の動画をまとめてつくったもの。
/// SNS へのアップロードは利用者が手動で行う（ReachForge は SNS とつながない）。
/// </summary>
public sealed class LpProject : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Url { get; set; }
    public string Title { get; set; } = "";
    public List<SocialPlatform> Platforms { get; set; } = [];
    public LpProjectStatus Status { get; set; } = LpProjectStatus.Creating;

    /// <summary>LP から取り込んだ画像（元の大きさ）。</summary>
    public List<Guid> SourceImageAssetIds { get; set; } = [];

    /// <summary>LP から選んだ画像と、何が写っているか（<see cref="SourceImageAssetIds"/> と同じ順）。</summary>
    public List<LpSourceImage> Sources { get; set; } = [];

    /// <summary>広告のビジュアル案と AI のキービジュアル。</summary>
    public List<LpVisual> Visuals { get; set; } = [];

    /// <summary>向きごとの動画（縦型 9:16・横型 16:9）。SNS ごとの使い分けは <see cref="LpPlatformOutput.Videos"/>。</summary>
    public Dictionary<MediaOrientation, Guid> Videos { get; set; } = [];

    /// <summary>動画をつくるか（画像は常につくる）。</summary>
    public bool MakeVideo { get; set; } = true;

    /// <summary>SNS ごとにつくったもの。</summary>
    public Dictionary<SocialPlatform, LpPlatformOutput> Outputs { get; set; } = [];

    /// <summary>画像と動画をつくるジョブ。</summary>
    public Guid? MediaJobId { get; set; }

    public string? Error { get; set; }
    public string CreatedBy { get; set; } = "";
}
