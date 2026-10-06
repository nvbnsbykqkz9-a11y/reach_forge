using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Abstractions;

public sealed record ProcessedImage(byte[] Bytes, string Mime, int Width, int Height);

/// <summary>
/// 画像処理（RF-DES-001 3.3 メディア処理）。実装は Infrastructure（ImageSharp）。
/// ライセンスの都合で他ライブラリ（SkiaSharp など）へ差し替えられるよう、この抽象越しにのみ使う。
/// </summary>
public interface IImageProcessor
{
    /// <summary>取り込み時の正規化：向きの補正、メタデータ（位置情報など）の削除、sRGB 化、最大辺の縮小、再エンコード。</summary>
    Task<ProcessedImage> NormalizeAsync(Stream input, int maxDimension, CancellationToken ct);

    /// <summary>比率変換（引き伸ばし禁止）：SmartCrop（被写体中心）または Pad（背景色の余白）。</summary>
    Task<ProcessedImage> ConvertAspectAsync(byte[] source, AspectRatio target, (int Width, int Height) size, AspectMethod method,
        string padColorHex, CancellationToken ct);

    /// <summary>アウトペインティング用のキャンバス（目標比率で、元画像の外側を透明にした PNG）。</summary>
    Task<ProcessedImage> PrepareOutpaintCanvasAsync(byte[] source, AspectRatio target, (int Width, int Height) size, CancellationToken ct);

    /// <summary>JPEG（sRGB）で、指定サイズ以下になるよう品質を調整して書き出す（Instagram：8MB 以下）。</summary>
    Task<ProcessedImage> EncodeJpegAsync(byte[] source, long maxBytes, CancellationToken ct);

    Task<ProcessedImage> ThumbnailAsync(byte[] source, int maxDimension, CancellationToken ct);

    /// <summary>ロゴを右下に正確に重ねる（生成 AI でロゴを描かせない：F-04-5）。</summary>
    Task<ProcessedImage> OverlayLogoAsync(byte[] source, byte[] logo, double widthRatio, CancellationToken ct);

    /// <summary>ローカル用スタブの画像（ブランド色のグラデーションと図形）。</summary>
    Task<ProcessedImage> RenderPlaceholderAsync(int width, int height, int seed, IReadOnlyList<string> colorsHex, CancellationToken ct);
}

/// <summary>メディアの保存先（Local：ディスク、本番：Azure Blob Storage）。</summary>
public interface IMediaStorage
{
    Task SaveAsync(string path, byte[] content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
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
