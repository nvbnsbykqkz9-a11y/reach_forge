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
/// <remarks><paramref name="Kind"/>：写真（商品・場面）か、サービスの画面（スクリーンショット）か。画面は AI で描き直さず、端末の枠に入れて使う。</remarks>
public sealed record LpSourceImage(Guid AssetId, string Url, string Description, LpSourceKind Kind = LpSourceKind.Photo);

/// <summary>LP の画像の種類。</summary>
public enum LpSourceKind
{
    /// <summary>商品・使っている場面・お店などの写真（AI で広告写真に仕上げる）。</summary>
    Photo = 0,

    /// <summary>アプリ・Web サービスの画面（文字がつぶれないよう、AI で描き直さずに端末の枠に入れる）。</summary>
    Screen = 1,
}

/// <summary>背景の模様（動画生成 AI が使えないときに描く背景の種類。LP の雰囲気から AI が選ぶ）。</summary>
public enum BackdropMotif
{
    /// <summary>流れるオーロラの帯（落ち着き・先進的）。</summary>
    Aurora = 0,

    /// <summary>うねる煙・霧（緊張感・課題）。</summary>
    Smoke = 1,

    /// <summary>中心から広がる光（解決・ひらめき）。</summary>
    Rays = 2,

    /// <summary>ぼけた光の粒（温かさ・華やかさ）。</summary>
    Bokeh = 3,

    /// <summary>やわらかな波・グラデーション（やさしさ・自然）。</summary>
    Waves = 4,
}

/// <summary>
/// LP の内容から AI が決めた広告の世界観：雰囲気・色（LP のブランドの色）・背景の模様・映像の舞台（動画生成 AI への指示、英語）。
/// </summary>
public sealed record LpArtDirection
{
    /// <summary>雰囲気（日本語。例：信頼感のある先進的な雰囲気）。</summary>
    public string Mood { get; init; } = "";

    /// <summary>色（#RRGGBB、濃い色から順に 2〜4 色）。</summary>
    public IReadOnlyList<string> Palette { get; init; } = [];

    public BackdropMotif Motif { get; init; }

    /// <summary>映像の舞台（英語。例：a dim security operations center at night with glowing monitors）。</summary>
    public string Setting { get; init; } = "";
}

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

    /// <summary>元の画像の種類（画面のスクリーンショットは端末の枠に入れる）。</summary>
    public LpSourceKind Kind { get; init; }

    /// <summary>背景の情景（英語。画面の後ろの背景と、動画の背景の映像に使う）。</summary>
    public string BackdropPrompt { get; init; } = "";

    /// <summary>AI の出来の確認で作り直したか。</summary>
    public bool Retried { get; init; }

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

    /// <summary>LP の内容から AI が決めた世界観（雰囲気・色・背景）。</summary>
    public LpArtDirection Direction { get; set; } = new();

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
