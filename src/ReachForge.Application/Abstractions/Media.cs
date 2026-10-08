using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Abstractions;

public sealed record ProcessedImage(byte[] Bytes, string Mime, int Width, int Height);

public enum TextPosition { Top = 1, Center = 2, Bottom = 3 }

/// <summary>画像に入れる文字（F-04 文字入れ）。帯の色に合わせて文字色は読みやすい方（白／黒）を自動で選ぶ。</summary>
/// <param name="SafeBottom">下からあける割合（0〜0.4）。ストーリーズ・リール・TikTok など、画面の下にアプリのボタンや説明文が重なる縦型で使う。</param>
public sealed record TextOverlay(string Headline, string? Sub, TextPosition Position, string BandColorHex, float BandOpacity = 0.78f,
    float SafeBottom = 0)
{
    /// <summary>縦型（9:16）で、アプリの表示に隠れないよう下からあける割合。</summary>
    public const float VerticalSafeBottom = 0.2f;

    public const int MaxHeadline = 30;
    public const int MaxSub = 40;
}

/// <summary>画像内の範囲（左上を 0,0、右下を 1,1 とする割合）。</summary>
public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X + Width <= 1.0001 && Y + Height <= 1.0001;
}

/// <summary>切り抜き（背景を透明にした画像）。被写体の範囲と、背景をどれだけ確かに分けられたかを返す。</summary>
public sealed record Cutout(ProcessedImage Image, NormalizedRect SubjectBounds, double Confidence)
{
    /// <summary>これ未満の確かさでは切り抜きを使わない（背景が複雑な写真）。</summary>
    public const double MinConfidence = 0.6;
}

public enum ProductPlacement { Center = 1, Bottom = 2, Left = 3, Right = 4 }

/// <summary>
/// 画像処理（RF-DES-001 3.3 メディア処理）。実装は Infrastructure（SkiaSharp）。
/// 画像処理ライブラリを差し替えられるよう、この抽象越しにのみ使う。
/// </summary>
public interface IImageProcessor
{
    /// <summary>取り込み時の正規化：向きの補正、メタデータ（位置情報など）の削除、sRGB 化、最大辺の縮小、再エンコード。</summary>
    Task<ProcessedImage> NormalizeAsync(Stream input, int maxDimension, CancellationToken ct);

    /// <summary>
    /// 比率変換（引き伸ばし禁止）：SmartCrop（被写体中心）または Pad（背景色の余白）。
    /// <paramref name="exact"/> が true なら、比率をそろえたうえで指定の大きさちょうどにする（SNS の入稿サイズ。少しの拡大を含む）。
    /// </summary>
    Task<ProcessedImage> ConvertAspectAsync(byte[] source, AspectRatio target, (int Width, int Height) size, AspectMethod method,
        string padColorHex, CancellationToken ct, bool exact = false);

    /// <summary>アウトペインティング用のキャンバス（目標比率で、元画像の外側を透明にした PNG）。</summary>
    Task<ProcessedImage> PrepareOutpaintCanvasAsync(byte[] source, AspectRatio target, (int Width, int Height) size, CancellationToken ct);

    /// <summary>JPEG（sRGB）で、指定サイズ以下になるよう品質を調整して書き出す（Instagram：8MB 以下）。</summary>
    Task<ProcessedImage> EncodeJpegAsync(byte[] source, long maxBytes, CancellationToken ct);

    Task<ProcessedImage> ThumbnailAsync(byte[] source, int maxDimension, CancellationToken ct);

    /// <summary>ロゴを右下に正確に重ねる（生成 AI でロゴを描かせない：F-04-5）。</summary>
    Task<ProcessedImage> OverlayLogoAsync(byte[] source, byte[] logo, double widthRatio, CancellationToken ct);

    /// <summary>
    /// 日本語の文字を画像に正確に描く（生成 AI に文字を描かせない）。帯の上に見出し・補足を置き、幅に収まるよう自動で縮める。
    /// </summary>
    Task<ProcessedImage> RenderTextAsync(byte[] source, TextOverlay overlay, CancellationToken ct);

    /// <summary>文字の帯だけを透明な画像（PNG）に描く。動画のクリップの上に重ねるテロップに使う。</summary>
    Task<ProcessedImage> RenderTextLayerAsync(TextOverlay overlay, (int Width, int Height) size, CancellationToken ct);

    /// <summary>単色の画像（JPEG）。素材の画像がないシーンの背景に使う。</summary>
    Task<ProcessedImage> CreateBackgroundAsync(string colorHex, (int Width, int Height) size, CancellationToken ct);

    /// <summary>不要物除去用のキャンバス：指定範囲を透明にした PNG（生成 AI に透明部分だけを描き直させる）。</summary>
    Task<ProcessedImage> PrepareEraseCanvasAsync(byte[] source, IReadOnlyList<NormalizedRect> regions, CancellationToken ct);

    /// <summary>
    /// 範囲外を元画像に戻す（AI が範囲外を変えても、元の画素を保つ）。<paramref name="keepMask"/> がある場合は、
    /// その不透明部分（切り抜いた被写体）を元画像のまま重ねる。出力の大きさは元画像に合わせる。
    /// </summary>
    Task<ProcessedImage> RestoreAsync(byte[] original, byte[] edited, IReadOnlyList<NormalizedRect>? editableRegions, byte[]? keepMask,
        CancellationToken ct);

    /// <summary>背景の切り抜き（周囲から背景色を推定して透明にする。単色に近い背景の商品写真向け）。</summary>
    Task<Cutout> CutoutAsync(byte[] source, CancellationToken ct);

    /// <summary>切り抜いた商品を背景に配置する（商品の画素は変えない。接地の影を付ける）。</summary>
    Task<ProcessedImage> CompositeAsync(byte[] background, byte[] cutout, ProductPlacement placement, double scale, CancellationToken ct);

    /// <summary>画像の大きさ（向きの補正後）。読めない画像は例外。</summary>
    Task<(int Width, int Height)> MeasureAsync(byte[] source, CancellationToken ct);

    /// <summary>
    /// 画像を切らずに、指定の大きさの中に全体を収める（余白は同じ画像を拡大してぼかした背景で埋める）。
    /// 比率の合わない画像を、縦型・横型の動画や広告の形式に入れるときに使う。
    /// </summary>
    Task<ProcessedImage> FitWithBackdropAsync(byte[] source, (int Width, int Height) size, CancellationToken ct);

    /// <summary>
    /// 広告の背景を描く（LP の色と雰囲気に合わせた、煙・光・オーロラ・光の粒などの模様）。生成 AI が使えないときの背景や、
    /// 画面（スクリーンショット）の後ろの背景に使う。<see cref="BackdropStyle.Time"/> を変えると模様が少しずつ動く。
    /// </summary>
    Task<ProcessedImage> RenderBackdropAsync((int Width, int Height) size, BackdropStyle style, CancellationToken ct);

    /// <summary>
    /// サービスの画面（スクリーンショット）を端末の枠（横長の画面はノートパソコン、縦長はスマートフォン）に入れて、背景の上に置く。
    /// 画面の画素は描き直さない（文字がつぶれない）。下には見出し・テロップの場所をあける。
    /// <paramref name="background"/> が null なら透明な画像（PNG）にする（動画の映像の上に重ねる）。<paramref name="caption"/> があれば文字の帯も描く。
    /// </summary>
    Task<ProcessedImage> ComposeDeviceAsync(byte[]? background, byte[] screen, (int Width, int Height) size, TextOverlay? caption,
        CancellationToken ct);

    /// <summary>ローカル用スタブの画像（ブランド色のグラデーションと図形）。</summary>
    Task<ProcessedImage> RenderPlaceholderAsync(int width, int height, int seed, IReadOnlyList<string> colorsHex, CancellationToken ct);
}

/// <summary>広告の背景の描き方（色は濃い色から順に。足りない色は補う）。</summary>
public sealed record BackdropStyle(IReadOnlyList<string> Colors, ReachForge.Domain.Entities.BackdropMotif Motif, int Seed, float Time = 0);

/// <summary>メディアの保存先（Local：ディスク、本番：Azure Blob Storage）。</summary>
public interface IMediaStorage
{
    Task SaveAsync(string path, byte[] content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>動画の1シーン（9:16・1080×1920 にテロップを焼き込んだ画像と、表示秒数・ナレーション）。</summary>
/// <summary>
/// 動画の1シーン。<paramref name="Clip"/>（生成 AI の動画）があれば静止画の代わりに使い、<paramref name="OverlayPng"/>（透明なテロップ）を重ねる。
/// <paramref name="Image"/> はサムネイル・クリップを使えないときの代わりにもなる。
/// </summary>
public sealed record VideoSceneInput(byte[] Image, double Seconds, byte[]? NarrationWav, byte[]? Clip = null, byte[]? OverlayPng = null);

public sealed record ComposedVideo(byte[] Mp4, int DurationMs, int Width, int Height);

/// <summary>
/// 動画の合成（F-05 ③ 処理 4〜5：FFmpeg）。9:16・1080×1920・H.264/AAC・faststart で書き出す。
/// </summary>
public interface IVideoComposer
{
    /// <param name="size">書き出す大きさ（既定は縦型 1080×1920。横型は 1920×1080）。シーンの画像はこの比率でつくっておく。</param>
    Task<ComposedVideo> ComposeAsync(IReadOnlyList<VideoSceneInput> scenes, CancellationToken ct, VideoAudioOptions? audio = null,
        (int Width, int Height)? size = null);

    /// <summary>生成 AI の動画クリップを 1080×1920・H.264/AAC・faststart に整え、BGM を重ねる（F-05 ①②）。</summary>
    Task<ComposedVideo> FinishClipAsync(byte[] clip, VideoAudioOptions? audio, CancellationToken ct);

    /// <summary>動画の1コマを JPEG で取り出す（サムネイル用）。</summary>
    Task<byte[]> ExtractFrameAsync(byte[] mp4, double seconds, CancellationToken ct);

    /// <summary>動画を書き出せるかを確かめる。書き出せない場合は、理由と対処を利用者向けの言葉で返す（書き出せれば null）。</summary>
    Task<string?> CheckAsync(CancellationToken ct);
}

/// <summary>BGM（F-05 処理 4：ライセンス済み素材をナレーションの間だけ下げて（ダッキング）合成）。</summary>
public sealed record VideoAudioOptions(string? BgmTrackId, double BgmVolume = 0.22);

/// <summary>BGM の曲。<see cref="License"/> は利用条件（画面に表示する）。</summary>
public sealed record BgmTrack(string Id, string Title, string License, bool BuiltIn);

/// <summary>使える BGM（組み込みの合成音源と、運用者が置いたライセンス済みの曲）。</summary>
public interface IBgmLibrary
{
    IReadOnlyList<BgmTrack> Tracks { get; }
}

public sealed record SafetyVerdict(bool Blocked, string Result)
{
    public static readonly SafetyVerdict NotChecked = new(false, "not_checked");
}

/// <summary>画像の有害性判定（Azure AI Content Safety など）。</summary>
public interface IImageSafetyChecker
{
    Task<SafetyVerdict> CheckAsync(byte[] image, string mime, CancellationToken ct);
}

/// <summary>IPTC の DigitalSourceType（AI 生成物の来歴。Meta などの AI ラベルの判定に使われる）。</summary>
public enum DigitalSourceType
{
    /// <summary>生成 AI が作ったもの（trainedAlgorithmicMedia）。</summary>
    TrainedAlgorithmicMedia,

    /// <summary>撮影・制作物に生成 AI の結果を合成・編集したもの（compositeWithTrainedAlgorithmicMedia）。</summary>
    CompositeWithTrainedAlgorithmicMedia,
}

/// <summary>来歴の情報（RF-DES-001 6章 c2pa_manifest、11章 AI ラベル）。</summary>
public sealed record ProvenanceInfo(DigitalSourceType SourceType, string Action, string? Provider, string? Model, DateTimeOffset When, string Title);

/// <summary>来歴を埋め込んだメディア。<see cref="Manifest"/> は C2PA のマニフェスト（JSON）、<see cref="Signed"/> は署名済みかどうか。</summary>
public sealed record StampedMedia(byte[] Bytes, string Manifest, bool Signed);

/// <summary>
/// AI 生成物への来歴の付与（F-04 処理 4「C2PA 来歴を付与」）。画像には IPTC の DigitalSourceType を XMP で埋め込み、
/// 署名の設定があれば C2PA マニフェストを署名して埋め込む。
/// </summary>
public interface IProvenanceStamper
{
    Task<StampedMedia> StampAsync(byte[] content, string mime, ProvenanceInfo info, CancellationToken ct);
}
