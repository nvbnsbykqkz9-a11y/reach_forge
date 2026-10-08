using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using SkiaSharp;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// SkiaSharp（MIT）による画像処理（RF-DES-001 3.3）。比率変換は「引き伸ばし禁止」：
/// 比率差 2% 以下は縮小のみ、それ以外は被写体を中心にしたスマートクロップ または 背景色の余白。
/// 読み込み時に向き（EXIF）を補正して sRGB に変換し、書き出しは再エンコードのため位置情報などのメタデータは残らない。
/// </summary>
public sealed partial class SkiaImageProcessor(IOptions<MediaOptions>? options = null) : IImageProcessor
{
    public const double AspectTolerance = 0.02;

    private readonly Lazy<SKTypeface?> _font = new(() => FindFont(options?.Value ?? new MediaOptions()));

    /// <summary>日本語フォントを探す（設定のファイル → OS のフォント）。日本語の字形を持たないフォントは使わない。</summary>
    private static SKTypeface? FindFont(MediaOptions o)
    {
        if (o.FontPath is { Length: > 0 } path && File.Exists(path) && SKTypeface.FromFile(path) is { } file && HasJapanese(file))
        {
            return file;
        }
        foreach (var name in o.FontFamilies)
        {
            var typeface = SKFontManager.Default.MatchFamily(name, SKFontStyle.Bold);
            if (typeface is not null && HasJapanese(typeface)) return typeface;
        }
        // 名前で探せない環境（フォントの一覧を使えない Linux など）では、よくある日本語フォントのファイルを探す
        foreach (var candidate in FontFiles())
        {
            if (SKTypeface.FromFile(candidate) is { } typeface && HasJapanese(typeface)) return typeface;
        }
        return null;

        static IEnumerable<string> FontFiles()
        {
            string[] dirs =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
                "/usr/share/fonts", "/usr/local/share/fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
            ];
            string[] names = ["NotoSansJP", "NotoSansCJK", "ipaexg", "ipag", "YuGoth", "meiryo", "msgothic"];
            return dirs.Where(d => d.Length > 0 && Directory.Exists(d))
                .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
                .Select(f => (File: f, Rank: Array.FindIndex(names, n => Path.GetFileName(f).Contains(n, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank)
                .Select(x => x.File);
        }

        static bool HasJapanese(SKTypeface t)
        {
            using var font = new SKFont(t);
            return font.ContainsGlyphs("あ字");
        }
    }

    public async Task<ProcessedImage> RenderTextAsync(byte[] source, TextOverlay overlay, CancellationToken ct)
    {
        using var image = Decode(source);
        DrawOverlay(image, overlay);
        return await JpegAsync(image, 92, ct);
    }

    public async Task<ProcessedImage> RenderTextLayerAsync(TextOverlay overlay, (int Width, int Height) size, CancellationToken ct)
    {
        using var image = NewBitmap(size.Width, size.Height, SKColors.Transparent);
        DrawOverlay(image, overlay);
        return await PngAsync(image, ct);
    }

    public async Task<ProcessedImage> CreateBackgroundAsync(string colorHex, (int Width, int Height) size, CancellationToken ct)
    {
        using var image = NewBitmap(size.Width, size.Height, Hex(colorHex));
        return await JpegAsync(image, 92, ct);
    }

    /// <summary>帯の上に見出し・補足を描く（幅に収まるよう自動で縮める。日本語は1文字単位で折り返す）。</summary>
    private void DrawOverlay(SKBitmap image, TextOverlay overlay)
    {
        var typeface = _font.Value ?? throw new InvalidOperationException(
            "日本語フォントが見つかりません。Media:FontPath に Noto Sans JP などのフォントファイルを設定してください。");
        var w = image.Width;
        var h = image.Height;
        var band = Hex(overlay.BandColorHex);
        // 帯の色に対してコントラストが高い方の文字色（WCAG の相対輝度で判定）
        var text = Luminance(band) > 0.4 ? SKColors.Black : SKColors.White;
        var padding = w * 0.06f;
        var maxWidth = w - padding * 2;

        // まず1行に収まる大きさを探し（読みやすさ優先・最小は幅の5.5%）、収まらなければ2行で折り返す
        (SKFont Font, List<string> Lines) Fit(string value, float startRatio)
        {
            foreach (var (maxLines, minRatio) in new[] { (1, 0.055f), (2, 0.03f) })
            {
                for (var size = w * startRatio; size >= w * minRatio; size *= 0.94f)
                {
                    var font = CreateFont(typeface, size);
                    var lines = Wrap(value, font, maxWidth);
                    if (lines.Count <= maxLines) return (font, lines);
                    font.Dispose();
                }
            }
            var smallest = CreateFont(typeface, w * 0.03f);
            return (smallest, Wrap(value, smallest, maxWidth));
        }

        var (headlineFont, headlineLines) = Fit(overlay.Headline, 0.085f);
        var (subFont, subLines) = overlay.Sub is { Length: > 0 } ? Fit(overlay.Sub, Math.Min(0.045f, headlineFont.Size / w * 0.6f)) : (null, []);
        try
        {
            var headlineHeight = headlineLines.Count * LineHeight(headlineFont);
            var subHeight = subFont is null ? 0 : subLines.Count * LineHeight(subFont);
            var gap = subFont is null ? 0 : headlineFont.Size * 0.35f;
            var bandHeight = headlineHeight + gap + subHeight + padding * 1.2f;
            var top = overlay.Position switch
            {
                TextPosition.Top => 0f,
                TextPosition.Center => (h - bandHeight) / 2,
                _ => h - bandHeight,
            };

            using var canvas = new SKCanvas(image);
            using (var fill = new SKPaint { Color = band.WithAlpha((byte)(255 * Math.Clamp(overlay.BandOpacity, 0.4f, 1f))), IsAntialias = true })
            {
                canvas.DrawRect(SKRect.Create(0, top, w, bandHeight), fill);
            }
            using var paint = new SKPaint { Color = text, IsAntialias = true };
            var y = top + padding * 0.6f;
            y = DrawLines(canvas, headlineLines, headlineFont, paint, w / 2f, y);
            if (subFont is not null) DrawLines(canvas, subLines, subFont, paint, w / 2f, y + gap);
            canvas.Flush();
        }
        finally
        {
            headlineFont.Dispose();
            subFont?.Dispose();
        }
    }

    /// <summary>行の高さ（折り返した行どうしが詰まらないよう、文字の大きさの 1.35 倍以上）。</summary>
    private static float LineHeight(SKFont font) => Math.Max(font.Spacing, font.Size * 1.35f);

    private static SKFont CreateFont(SKTypeface typeface, float size) =>
        new(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias, Embolden = typeface.FontWeight < (int)SKFontStyleWeight.SemiBold };

    /// <summary>中央揃えで行を描き、次の行の上端を返す。</summary>
    private static float DrawLines(SKCanvas canvas, IReadOnlyList<string> lines, SKFont font, SKPaint paint, float centerX, float top)
    {
        foreach (var line in lines)
        {
            var height = LineHeight(font);
            // 行の高さの中で、文字（アセント〜ディセント）を上下中央に置く
            var baseline = top + (height - (font.Metrics.Descent - font.Metrics.Ascent)) / 2 - font.Metrics.Ascent;
            canvas.DrawText(line, centerX, baseline, SKTextAlign.Center, font, paint);
            top += height;
        }
        return top;
    }

    /// <summary>行頭に置かない文字（句読点・閉じ括弧・小書きの仮名・長音）。</summary>
    private const string NoLineStart = "、。，．・：；？！ー）」』】〕〉》’”ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ!?,.:;)]}%";

    /// <summary>
    /// 幅に収まるよう折り返す。日本語は1文字単位、英数字の語は途中で切らない。行頭に句読点などが来る場合は前の文字ごと次の行へ送る。
    /// </summary>
    internal static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var rest = paragraph.Trim();
            while (rest.Length > 0)
            {
                if (font.MeasureText(rest) <= maxWidth)
                {
                    lines.Add(rest);
                    break;
                }
                // 収まる最長の長さ（文字の区切り単位で数える：サロゲートペアを分けない）
                var cut = 0;
                var elements = System.Globalization.StringInfo.ParseCombiningCharacters(rest);
                for (var i = 1; i <= elements.Length; i++)
                {
                    var end = i == elements.Length ? rest.Length : elements[i];
                    if (font.MeasureText(rest[..end]) > maxWidth) break;
                    cut = end;
                }
                if (cut == 0) cut = elements.Length > 1 ? elements[1] : rest.Length; // 1文字も入らない幅でも進める
                // 英数字の語の途中なら、語の前（空白）で切る
                if (cut < rest.Length && IsWordChar(rest[cut]) && IsWordChar(rest[cut - 1]))
                {
                    var space = rest.LastIndexOf(' ', cut - 1);
                    if (space > 0) cut = space;
                }
                // 行頭禁則：次の行の先頭が句読点などなら、前の文字ごと送る
                while (cut > 1 && cut < rest.Length && NoLineStart.Contains(rest[cut])) cut--;
                lines.Add(rest[..cut].TrimEnd());
                rest = rest[cut..].TrimStart();
            }
        }
        return lines.Count == 0 ? [""] : lines;

        static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c);
    }

    private static double Luminance(SKColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }

    public async Task<ProcessedImage> NormalizeAsync(Stream input, int maxDimension, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        using var image = Decode(ms.ToArray());
        using var fitted = FitWithin(image, maxDimension, maxDimension);
        return HasTransparency(fitted) ? await PngAsync(fitted, ct) : await JpegAsync(fitted, 90, ct);
    }

    public async Task<ProcessedImage> ConvertAspectAsync(byte[] source, AspectRatio target, (int Width, int Height) size,
        AspectMethod method, string padColorHex, CancellationToken ct)
    {
        using var image = Decode(source);
        var current = (double)image.Width / image.Height;
        SKBitmap converted;
        if (Math.Abs(current - target.Value) / target.Value <= AspectTolerance)
        {
            converted = image.Copy();
        }
        else if (method == AspectMethod.Pad)
        {
            var (w, h) = CanvasFor(image.Width, image.Height, target.Value);
            converted = Draw(w, h, Hex(padColorHex), c => c.DrawBitmap(image, (w - image.Width) / 2, (h - image.Height) / 2, SKSamplingOptions.Default));
        }
        else
        {
            converted = Crop(image, SmartCropRectangle(image, target.Value));
        }

        // 目標サイズより大きい場合のみ縮小する（小さい画像を拡大してぼかさない）
        using (converted)
        {
            using var fitted = FitWithin(converted, size.Width, size.Height);
            return await PngAsync(fitted, ct);
        }
    }

    public async Task<ProcessedImage> PrepareOutpaintCanvasAsync(byte[] source, AspectRatio target, (int Width, int Height) size,
        CancellationToken ct)
    {
        using var image = Decode(source);
        var (w, h) = CanvasFor(image.Width, image.Height, target.Value);
        using var canvas = Draw(w, h, SKColors.Transparent, c => c.DrawBitmap(image, (w - image.Width) / 2, (h - image.Height) / 2, SKSamplingOptions.Default));
        using var fitted = FitWithin(canvas, size.Width, size.Height);
        return await PngAsync(fitted, ct);
    }

    public async Task<ProcessedImage> EncodeJpegAsync(byte[] source, long maxBytes, CancellationToken ct)
    {
        var image = Decode(source);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                foreach (var quality in new[] { 90, 82, 74, 66 })
                {
                    var result = await JpegAsync(image, quality, ct);
                    if (result.Bytes.LongLength <= maxBytes) return result;
                }
                if (attempt >= 6) throw new InvalidOperationException("画像を指定サイズ以下にできませんでした。");
                var smaller = Resize(image, Math.Max(1, (int)(image.Width * 0.85)), Math.Max(1, (int)(image.Height * 0.85)));
                image.Dispose();
                image = smaller;
            }
        }
        finally
        {
            image.Dispose();
        }
    }

    public async Task<ProcessedImage> ThumbnailAsync(byte[] source, int maxDimension, CancellationToken ct)
    {
        using var image = Decode(source);
        using var fitted = FitWithin(image, maxDimension, maxDimension);
        return await PngAsync(fitted, ct);
    }

    public async Task<ProcessedImage> OverlayLogoAsync(byte[] source, byte[] logo, double widthRatio, CancellationToken ct)
    {
        using var image = Decode(source);
        using var mark = Decode(logo);
        var w = Math.Max(16, (int)(image.Width * widthRatio));
        using var resized = Resize(mark, w, Math.Max(1, (int)Math.Round((double)mark.Height * w / mark.Width)));
        var margin = (int)(Math.Min(image.Width, image.Height) * 0.03);
        using (var canvas = new SKCanvas(image))
        {
            canvas.DrawBitmap(resized, image.Width - resized.Width - margin, image.Height - resized.Height - margin, SKSamplingOptions.Default);
        }
        return HasTransparency(image) ? await PngAsync(image, ct) : await JpegAsync(image, 90, ct);
    }

    public async Task<ProcessedImage> RenderPlaceholderAsync(int width, int height, int seed, IReadOnlyList<string> colorsHex,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var palette = colorsHex.Select(Hex).ToList();
        while (palette.Count < 3)
        {
            palette.Add(new SKColor((byte)random.Next(60, 230), (byte)random.Next(60, 230), (byte)random.Next(60, 230)));
        }

        var circles = Enumerable.Range(0, 5)
            .Select(_ => (X: random.Next(width), Y: random.Next(height), R: random.Next(Math.Min(width, height) / 8, Math.Min(width, height) / 3),
                Color: palette[random.Next(palette.Count)]))
            .ToList();
        var pixels = new Rgba(width, height);
        for (var y = 0; y < height; y++)
        {
            var t = (float)y / height;
            for (var x = 0; x < width; x++)
            {
                var pixel = Lerp(palette[0], palette[1], (t + (float)x / width) / 2);
                foreach (var c in circles)
                {
                    var d2 = (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y);
                    if (d2 < c.R * c.R) pixel = Lerp(pixel, c.Color, 0.55f);
                }
                pixels[x, y] = pixel;
            }
        }
        using var image = pixels.ToBitmap();
        return await PngAsync(image, ct);
    }

    /// <summary>
    /// スマートクロップ：縮小したグレースケール画像の輪郭の強さ（勾配）と彩度を「注目度」とし、
    /// 目標比率の切り抜き枠のうち注目度の合計が最大になる位置を選ぶ（中央に少し寄せる）。
    /// </summary>
    internal static SKRectI SmartCropRectangle(SKBitmap image, double targetRatio)
    {
        var (cw, ch) = image.Width / (double)image.Height > targetRatio
            ? ((int)Math.Round(image.Height * targetRatio), image.Height)
            : (image.Width, (int)Math.Round(image.Width / targetRatio));
        cw = Math.Clamp(cw, 1, image.Width);
        ch = Math.Clamp(ch, 1, image.Height);
        var horizontal = cw < image.Width;
        var range = horizontal ? image.Width - cw : image.Height - ch;
        if (range <= 0) return SKRectI.Create(0, 0, cw, ch);

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
        return horizontal ? SKRectI.Create(offset, 0, cw, ch) : SKRectI.Create(0, offset, cw, ch);
    }

    /// <summary>切り抜き方向に沿った注目度の分布（列または行ごとの合計）。</summary>
    private static double[] SaliencyProfile(SKBitmap image, bool horizontal)
    {
        const int sampleSize = 160;
        var ratio = Math.Min(1.0, (double)sampleSize / Math.Max(image.Width, image.Height));
        using var small = Resize(image, Math.Max(1, (int)Math.Round(image.Width * ratio)), Math.Max(1, (int)Math.Round(image.Height * ratio)));
        var pixels = Rgba.From(small);
        var w = pixels.Width;
        var h = pixels.Height;
        var gray = new float[w * h];
        var sat = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = pixels[x, y];
                gray[y * w + x] = 0.299f * p.Red + 0.587f * p.Green + 0.114f * p.Blue;
                var max = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
                var min = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
                sat[y * w + x] = max == 0 ? 0 : (max - min) / (float)max;
            }
        }

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

    private static (int Width, int Height) CanvasFor(int width, int height, double ratio) =>
        width / (double)height > ratio
            ? (width, (int)Math.Round(width / ratio))
            : ((int)Math.Round(height * ratio), height);

    // ---- SkiaSharp の基本操作 ----

    private static readonly SKImageInfo s_rgba = new(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());

    /// <summary>読み込む（最初のコマだけ）。EXIF の向きを補正し、sRGB・RGBA に揃える。</summary>
    internal static SKBitmap Decode(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("画像を読み込めませんでした（対応していない形式か、壊れたファイルです）。");
        var info = s_rgba.WithSize(codec.Info.Width, codec.Info.Height);
        var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            throw new InvalidDataException($"画像を読み込めませんでした（{result}）。");
        }
        return codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? decoded : Orient(decoded, codec.EncodedOrigin);
    }

    /// <summary>EXIF の向きのとおりに回転・反転する。</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? source.Height : source.Width, h = swap ? source.Width : source.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        using (source)
        {
            return Draw(w, h, SKColors.Transparent, c =>
            {
                c.SetMatrix(matrix);
                c.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
            });
        }
    }

    private static SKBitmap NewBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(s_rgba.WithSize(width, height));
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>新しい画像に描く。</summary>
    private static SKBitmap Draw(int width, int height, SKColor background, Action<SKCanvas> draw)
    {
        var bitmap = NewBitmap(width, height, background);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI rect) =>
        Draw(rect.Width, rect.Height, SKColors.Transparent, c => c.DrawBitmap(source, -rect.Left, -rect.Top, SKSamplingOptions.Default));

    /// <summary>
    /// 大きさを変える。大きく縮める場合は半分ずつ縮めてから仕上げる（細部のちらつき・モアレを防ぐ）。
    /// </summary>
    internal static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var current = source;
        try
        {
            while (current.Width >= width * 2 && current.Height >= height * 2)
            {
                var half = Scale(current, current.Width / 2, current.Height / 2, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (!ReferenceEquals(current, source)) current.Dispose();
                current = half;
            }
            return Scale(current, width, height, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        finally
        {
            if (!ReferenceEquals(current, source)) current.Dispose();
        }

        static SKBitmap Scale(SKBitmap from, int w, int h, SKSamplingOptions sampling)
        {
            using var image = SKImage.FromBitmap(from);
            return Draw(w, h, SKColors.Transparent, c =>
            {
                using var paint = new SKPaint { IsAntialias = true };
                c.DrawImage(image, SKRect.Create(0, 0, w, h), sampling, paint);
            });
        }
    }

    /// <summary>枠に収まるよう縮める（拡大はしない）。常に新しい画像を返す。</summary>
    private static SKBitmap FitWithin(SKBitmap image, int maxWidth, int maxHeight)
    {
        if (image.Width <= maxWidth && image.Height <= maxHeight) return image.Copy();
        var ratio = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
        return Resize(image, Math.Max(1, (int)Math.Round(image.Width * ratio)), Math.Max(1, (int)Math.Round(image.Height * ratio)));
    }

    private static bool HasTransparency(SKBitmap image)
    {
        var span = image.GetPixelSpan();
        for (var i = 3; i < span.Length; i += 4)
        {
            if (span[i] < 255) return true;
        }
        return false;
    }

    private static SKColor Hex(string hex) =>
        SKColor.TryParse(hex, out var color) ? color : throw new ArgumentException($"色の指定が正しくありません：{hex}", nameof(hex));

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), 255);

    /// <summary>JPEG（透明な部分は白にする）。</summary>
    private static Task<ProcessedImage> JpegAsync(SKBitmap image, int quality, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var flat = Draw(image.Width, image.Height, SKColors.White, c => c.DrawBitmap(image, 0, 0, SKSamplingOptions.Default));
        return Task.FromResult(Encode(flat, SKEncodedImageFormat.Jpeg, quality, "image/jpeg"));
    }

    private static Task<ProcessedImage> PngAsync(SKBitmap image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Encode(image, SKEncodedImageFormat.Png, 100, "image/png"));
    }

    private static ProcessedImage Encode(SKBitmap image, SKEncodedImageFormat format, int quality, string mime)
    {
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException("画像を書き出せませんでした。");
        return new ProcessedImage(data.ToArray(), mime, image.Width, image.Height);
    }

    /// <summary>
    /// 画素の配列（RGBA・アルファは乗算しない値）。切り抜き・範囲の復元など、画素を直接扱う処理に使う。
    /// </summary>
    internal sealed class Rgba(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Data { get; } = new byte[width * height * 4];

        public SKColor this[int x, int y]
        {
            get
            {
                var i = (y * Width + x) * 4;
                return new SKColor(Data[i], Data[i + 1], Data[i + 2], Data[i + 3]);
            }
            set
            {
                var i = (y * Width + x) * 4;
                Data[i] = value.Red;
                Data[i + 1] = value.Green;
                Data[i + 2] = value.Blue;
                Data[i + 3] = value.Alpha;
            }
        }

        public static Rgba From(SKBitmap bitmap)
        {
            var pixels = new Rgba(bitmap.Width, bitmap.Height);
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
            var handle = GCHandle.Alloc(pixels.Data, GCHandleType.Pinned);
            try
            {
                using var pixmap = bitmap.PeekPixels();
                if (!pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes)) throw new InvalidOperationException("画素を読めませんでした。");
            }
            finally
            {
                handle.Free();
            }
            return pixels;
        }

        public SKBitmap ToBitmap()
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
            var handle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            try
            {
                using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
                var bitmap = new SKBitmap(s_rgba.WithSize(Width, Height));
                if (!pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)) throw new InvalidOperationException("画素を書けませんでした。");
                return bitmap;
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
