using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using Microsoft.Extensions.Options;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// ImageSharp による画像処理（RF-DES-001 3.3）。比率変換は「引き伸ばし禁止」：
/// 比率差 2% 以下は縮小のみ、それ以外は被写体を中心にしたスマートクロップ または 背景色の余白。
/// </summary>
public sealed class ImageSharpProcessor(IOptions<MediaOptions>? options = null) : IImageProcessor
{
    public const double AspectTolerance = 0.02;

    private readonly Lazy<FontFamily?> _font = new(() => FindFont(options?.Value ?? new MediaOptions()));

    /// <summary>日本語フォントを探す（設定のファイル → OS のフォント）。見つからなければ null。</summary>
    private static FontFamily? FindFont(MediaOptions o)
    {
        if (o.FontPath is { Length: > 0 } path && File.Exists(path))
        {
            var collection = new FontCollection();
            return collection.Add(path);
        }
        foreach (var name in o.FontFamilies)
        {
            if (SystemFonts.TryGet(name, out var family)) return family;
        }
        return null;
    }

    public async Task<ProcessedImage> RenderTextAsync(byte[] source, TextOverlay overlay, CancellationToken ct)
    {
        var family = _font.Value ?? throw new InvalidOperationException(
            "日本語フォントが見つかりません。Media:FontPath に Noto Sans JP などのフォントファイルを設定してください。");
        using var image = Image.Load<Rgba32>(s_decoder, source);
        var w = image.Width;
        var h = image.Height;
        var band = Color.ParseHex(overlay.BandColorHex).ToPixel<Rgba32>();
        // 帯の色に対してコントラストが高い方の文字色（WCAG の相対輝度で判定）
        var text = Luminance(band) > 0.4 ? Color.Black : Color.White;
        var padding = w * 0.06f;
        var maxWidth = w - padding * 2;

        // まず1行に収まる大きさを探し（読みやすさ優先・最小は幅の5.5%）、収まらなければ2行で折り返す
        Font Fit(string value, float startRatio)
        {
            foreach (var (maxLines, minRatio) in new[] { (1, 0.055f), (2, 0.03f) })
            {
                for (var size = w * startRatio; size >= w * minRatio; size *= 0.94f)
                {
                    var font = family.CreateFont(size, FontStyle.Bold);
                    var bounds = TextMeasurer.MeasureSize(value, new TextOptions(font) { WrappingLength = maxWidth });
                    if (bounds.Width <= maxWidth && bounds.Height <= size * 1.3f * maxLines) return font;
                }
            }
            return family.CreateFont(w * 0.03f, FontStyle.Bold);
        }

        var headlineFont = Fit(overlay.Headline, 0.085f);
        var subFont = overlay.Sub is { Length: > 0 } ? Fit(overlay.Sub, Math.Min(0.045f, headlineFont.Size / w * 0.6f)) : null;
        var headlineSize = TextMeasurer.MeasureSize(overlay.Headline, new TextOptions(headlineFont) { WrappingLength = maxWidth });
        var subSize = subFont is null ? FontRectangle.Empty : TextMeasurer.MeasureSize(overlay.Sub!, new TextOptions(subFont) { WrappingLength = maxWidth });
        var gap = subFont is null ? 0 : headlineFont.Size * 0.35f;
        var bandHeight = headlineSize.Height + gap + subSize.Height + padding * 1.2f;
        var top = overlay.Position switch
        {
            TextPosition.Top => 0f,
            TextPosition.Center => (h - bandHeight) / 2,
            _ => h - bandHeight,
        };

        image.Mutate(x =>
        {
            x.Fill(Color.FromPixel(new Rgba32(band.R, band.G, band.B, (byte)(255 * Math.Clamp(overlay.BandOpacity, 0.4f, 1f)))),
                new RectangleF(0, top, w, bandHeight));
            var y = top + padding * 0.6f;
            x.DrawText(new RichTextOptions(headlineFont)
            {
                Origin = new PointF(w / 2f, y), WrappingLength = maxWidth,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            }, overlay.Headline, text);
            if (subFont is not null)
            {
                x.DrawText(new RichTextOptions(subFont)
                {
                    Origin = new PointF(w / 2f, y + headlineSize.Height + gap), WrappingLength = maxWidth,
                    HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                }, overlay.Sub!, text);
            }
        });
        return await JpegAsync(image, 92, ct);
    }

    private static double Luminance(Rgba32 c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static readonly DecoderOptions s_decoder = new() { MaxFrames = 1 };

    public async Task<ProcessedImage> NormalizeAsync(Stream input, int maxDimension, CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgba32>(s_decoder, input, ct);
        image.Mutate(x => x.AutoOrient());
        StripMetadata(image);
        FitWithin(image, maxDimension, maxDimension);
        return HasTransparency(image) ? await PngAsync(image, ct) : await JpegAsync(image, 90, ct);
    }

    public async Task<ProcessedImage> ConvertAspectAsync(byte[] source, AspectRatio target, (int Width, int Height) size,
        AspectMethod method, string padColorHex, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        var current = (double)image.Width / image.Height;

        if (Math.Abs(current - target.Value) / target.Value > AspectTolerance)
        {
            if (method == AspectMethod.Pad)
            {
                Pad(image, target, Color.ParseHex(padColorHex));
            }
            else
            {
                image.Mutate(x => x.Crop(SmartCropRectangle(image, target.Value)));
            }
        }

        // 目標サイズより大きい場合のみ縮小する（小さい画像を拡大してぼかさない）
        FitWithin(image, size.Width, size.Height);
        return await PngAsync(image, ct);
    }

    public async Task<ProcessedImage> PrepareOutpaintCanvasAsync(byte[] source, AspectRatio target, (int Width, int Height) size,
        CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        var (w, h) = CanvasFor(image.Width, image.Height, target.Value);
        using var canvas = new Image<Rgba32>(w, h, Color.Transparent.ToPixel<Rgba32>());
        canvas.Mutate(x => x.DrawImage(image, new Point((w - image.Width) / 2, (h - image.Height) / 2), 1f));
        FitWithin(canvas, size.Width, size.Height);
        return await PngAsync(canvas, ct);
    }

    public async Task<ProcessedImage> EncodeJpegAsync(byte[] source, long maxBytes, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        StripMetadata(image);
        FlattenOnto(image, Color.White);
        for (var attempt = 0; ; attempt++)
        {
            foreach (var quality in new[] { 90, 82, 74, 66 })
            {
                var result = await JpegAsync(image, quality, ct);
                if (result.Bytes.LongLength <= maxBytes) return result;
            }
            if (attempt >= 6) throw new InvalidOperationException("画像を指定サイズ以下にできませんでした。");
            image.Mutate(x => x.Resize((int)(image.Width * 0.85), (int)(image.Height * 0.85)));
        }
    }

    public async Task<ProcessedImage> ThumbnailAsync(byte[] source, int maxDimension, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        FitWithin(image, maxDimension, maxDimension);
        return await PngAsync(image, ct);
    }

    public async Task<ProcessedImage> OverlayLogoAsync(byte[] source, byte[] logo, double widthRatio, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        using var mark = Image.Load<Rgba32>(s_decoder, logo);
        var w = Math.Max(16, (int)(image.Width * widthRatio));
        mark.Mutate(x => x.Resize(w, 0));
        var margin = (int)(Math.Min(image.Width, image.Height) * 0.03);
        image.Mutate(x => x.DrawImage(mark, new Point(image.Width - mark.Width - margin, image.Height - mark.Height - margin), 1f));
        return HasTransparency(image) ? await PngAsync(image, ct) : await JpegAsync(image, 90, ct);
    }

    public async Task<ProcessedImage> RenderPlaceholderAsync(int width, int height, int seed, IReadOnlyList<string> colorsHex,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var palette = colorsHex.Select(c => Color.ParseHex(c).ToPixel<Rgba32>()).ToList();
        while (palette.Count < 3)
        {
            palette.Add(new Rgba32((byte)random.Next(60, 230), (byte)random.Next(60, 230), (byte)random.Next(60, 230)));
        }

        using var image = new Image<Rgba32>(width, height);
        var circles = Enumerable.Range(0, 5)
            .Select(_ => (X: random.Next(width), Y: random.Next(height), R: random.Next(Math.Min(width, height) / 8, Math.Min(width, height) / 3),
                Color: palette[random.Next(palette.Count)]))
            .ToList();
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                var t = (float)y / rows.Height;
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = Lerp(palette[0], palette[1], (t + (float)x / row.Length) / 2);
                    foreach (var c in circles)
                    {
                        var d2 = (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y);
                        if (d2 < c.R * c.R) pixel = Lerp(pixel, c.Color, 0.55f);
                    }
                    row[x] = pixel;
                }
            }
        });
        return await PngAsync(image, ct);
    }

    /// <summary>
    /// スマートクロップ：縮小したグレースケール画像の輪郭の強さ（勾配）と彩度を「注目度」とし、
    /// 目標比率の切り抜き枠のうち注目度の合計が最大になる位置を選ぶ（中央に少し寄せる）。
    /// </summary>
    internal static Rectangle SmartCropRectangle(Image<Rgba32> image, double targetRatio)
    {
        var (cw, ch) = image.Width / (double)image.Height > targetRatio
            ? ((int)Math.Round(image.Height * targetRatio), image.Height)
            : (image.Width, (int)Math.Round(image.Width / targetRatio));
        cw = Math.Clamp(cw, 1, image.Width);
        ch = Math.Clamp(ch, 1, image.Height);
        var horizontal = cw < image.Width;
        var range = horizontal ? image.Width - cw : image.Height - ch;
        if (range <= 0) return new Rectangle(0, 0, cw, ch);

        var profile = SaliencyProfile(image, horizontal);
        var scale = (double)profile.Length / (horizontal ? image.Width : image.Height);
        var window = Math.Max(1, (int)Math.Round((horizontal ? cw : ch) * scale));
        var steps = profile.Length - window;

        var prefix = new double[profile.Length + 1];
        for (var i = 0; i < profile.Length; i++) prefix[i + 1] = prefix[i] + profile[i];

        var best = steps / 2;
        var bestScore = double.MinValue;
        for (var start = 0; start <= steps; start++)
        {
            var energy = prefix[start + window] - prefix[start];
            var centerBias = 1 - 0.15 * Math.Abs(start - steps / 2.0) / Math.Max(1, steps / 2.0);
            var score = energy * centerBias;
            if (score > bestScore)
            {
                bestScore = score;
                best = start;
            }
        }

        var offset = Math.Clamp((int)Math.Round(best / scale), 0, range);
        return horizontal ? new Rectangle(offset, 0, cw, ch) : new Rectangle(0, offset, cw, ch);
    }

    /// <summary>切り抜き方向に沿った注目度の分布（列または行ごとの合計）。</summary>
    private static double[] SaliencyProfile(Image<Rgba32> image, bool horizontal)
    {
        const int sampleSize = 160;
        using var small = image.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new Size(sampleSize, sampleSize),
            Mode = ResizeMode.Max,
        }));
        var w = small.Width;
        var h = small.Height;
        var gray = new float[w * h];
        var sat = new float[w * h];
        small.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    gray[y * w + x] = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
                    var max = Math.Max(p.R, Math.Max(p.G, p.B));
                    var min = Math.Min(p.R, Math.Min(p.G, p.B));
                    sat[y * w + x] = max == 0 ? 0 : (max - min) / (float)max;
                }
            }
        });

        var profile = new double[horizontal ? w : h];
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var gx = gray[y * w + x + 1] - gray[y * w + x - 1];
                var gy = gray[(y + 1) * w + x] - gray[(y - 1) * w + x];
                var energy = Math.Abs(gx) + Math.Abs(gy) + 40 * sat[y * w + x];
                profile[horizontal ? x : y] += energy;
            }
        }
        return profile;
    }

    private static void Pad(Image<Rgba32> image, AspectRatio target, Color background)
    {
        var (w, h) = CanvasFor(image.Width, image.Height, target.Value);
        image.Mutate(x => x.Pad(w, h, background));
    }

    private static (int Width, int Height) CanvasFor(int width, int height, double ratio) =>
        width / (double)height > ratio
            ? (width, (int)Math.Round(width / ratio))
            : ((int)Math.Round(height * ratio), height);

    private static void FitWithin(Image image, int maxWidth, int maxHeight)
    {
        if (image.Width <= maxWidth && image.Height <= maxHeight) return;
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(maxWidth, maxHeight),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3,
        }));
    }

    private static void StripMetadata(Image image)
    {
        // 位置情報などを含む EXIF・IPTC・XMP を削除する（個人情報保護）
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IccProfile = null;
    }

    private static bool HasTransparency(Image<Rgba32> image)
    {
        var transparent = false;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height && !transparent; y++)
            {
                foreach (var p in rows.GetRowSpan(y))
                {
                    if (p.A < 255)
                    {
                        transparent = true;
                        break;
                    }
                }
            }
        });
        return transparent;
    }

    private static void FlattenOnto(Image<Rgba32> image, Color background)
    {
        if (!HasTransparency(image)) return;
        image.Mutate(x => x.BackgroundColor(background));
    }

    private static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), 255);

    private static async Task<ProcessedImage> JpegAsync(Image image, int quality, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = quality }, ct);
        return new ProcessedImage(ms.ToArray(), "image/jpeg", image.Width, image.Height);
    }

    private static async Task<ProcessedImage> PngAsync(Image image, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms, new PngEncoder(), ct);
        return new ProcessedImage(ms.ToArray(), "image/png", image.Width, image.Height);
    }
}
