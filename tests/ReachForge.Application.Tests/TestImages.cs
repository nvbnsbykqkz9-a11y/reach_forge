using System.Text;
using ReachForge.Infrastructure.Media;
using SkiaSharp;

namespace ReachForge.Application.Tests;

/// <summary>テスト用の画像を作る・読む（SkiaSharp）。</summary>
internal static class TestImages
{
    /// <summary>単色の画像に、<paramref name="paint"/> で画素を塗って PNG（または JPEG）にする。</summary>
    public static byte[] Create(int width, int height, SKColor background, Func<int, int, SKColor?>? paint = null,
        SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var pixels = new SkiaImageProcessor.Rgba(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) pixels[x, y] = paint?.Invoke(x, y) ?? background;
        }
        using var bitmap = pixels.ToBitmap();
        using var data = bitmap.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>画素を読む（向きの補正・sRGB 化はアプリと同じ）。</summary>
    public static SkiaImageProcessor.Rgba Read(byte[] bytes)
    {
        using var bitmap = SkiaImageProcessor.Decode(bytes);
        return SkiaImageProcessor.Rgba.From(bitmap);
    }

    /// <summary>埋め込まれた XMP（JPEG の APP1・PNG の iTXt。どちらも圧縮しない）。なければ null。</summary>
    public static string? Xmp(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        var end = text.IndexOf("</x:xmpmeta>", StringComparison.Ordinal);
        return start < 0 || end < start ? null : text[start..(end + "</x:xmpmeta>".Length)];
    }

    /// <summary>JPEG に EXIF（向きとソフトウェア名）を入れる。</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new List<byte>();
        tiff.AddRange("II*\0"u8.ToArray());
        tiff.AddRange(BitConverter.GetBytes(8));               // 最初の IFD の位置
        tiff.AddRange(BitConverter.GetBytes((ushort)2));       // 項目数
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0112));  // Orientation（SHORT・1個）
        tiff.AddRange(BitConverter.GetBytes((ushort)3));
        tiff.AddRange(BitConverter.GetBytes(1));
        tiff.AddRange(BitConverter.GetBytes(orientation));
        tiff.AddRange(new byte[2]);
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0131));  // Software（ASCII・4文字）
        tiff.AddRange(BitConverter.GetBytes((ushort)2));
        tiff.AddRange(BitConverter.GetBytes(4));
        tiff.AddRange("cam\0"u8.ToArray());
        tiff.AddRange(BitConverter.GetBytes(0));               // 次の IFD なし
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }
}
