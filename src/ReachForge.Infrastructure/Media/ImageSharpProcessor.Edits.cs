using ReachForge.Application.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReachForge.Infrastructure.Media;

/// <summary>参照画像の編集（F-04：背景差替・不要物除去・商品の配置）のための画素処理。</summary>
public sealed partial class ImageSharpProcessor
{
    /// <summary>範囲の境界をなじませる幅（画素）。</summary>
    public const int BlendBand = 8;

    public async Task<ProcessedImage> PrepareEraseCanvasAsync(byte[] source, IReadOnlyList<NormalizedRect> regions, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        var rects = regions.Select(r => ToPixels(r, image.Width, image.Height)).ToList();
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (rects.Any(r => r.Contains(x, y))) row[x] = new Rgba32(0, 0, 0, 0);
                }
            }
        });
        return await PngAsync(image, ct);
    }

    public async Task<ProcessedImage> RestoreAsync(byte[] original, byte[] edited, IReadOnlyList<NormalizedRect>? editableRegions,
        byte[]? keepMask, CancellationToken ct)
    {
        using var source = Image.Load<Rgba32>(s_decoder, original);
        using var result = Image.Load<Rgba32>(s_decoder, edited);
        if (result.Width != source.Width || result.Height != source.Height)
        {
            result.Mutate(x => x.Resize(source.Width, source.Height));
        }
        using var mask = keepMask is null ? null : Image.Load<Rgba32>(s_decoder, keepMask);
        if (mask is not null && (mask.Width != source.Width || mask.Height != source.Height))
        {
            mask.Mutate(x => x.Resize(source.Width, source.Height));
        }
        var rects = editableRegions?.Select(r => ToPixels(r, source.Width, source.Height)).ToList();

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                // 編集してよい度合い（0：元のまま 〜 1：AI の結果）
                var editable = 1f;
                if (rects is not null)
                {
                    editable = rects.Count == 0 ? 0 : rects.Max(r => Inside(r, x, y));
                }
                if (mask is not null) editable *= 1f - mask[x, y].A / 255f;
                if (editable >= 1f) continue;
                result[x, y] = Blend(source[x, y], result[x, y], editable);
            }
        }
        StripMetadata(result);
        return await PngAsync(result, ct);
    }

    public async Task<Cutout> CutoutAsync(byte[] source, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(s_decoder, source);
        int w = image.Width, h = image.Height;
        var background = new bool[w * h];

        if (BorderTransparency(image) >= 0.5)
        {
            // すでに切り抜かれた PNG はそのまま使う
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                background[y * w + x] = image[x, y].A < 16;
        }
        else
        {
            var border = BorderPixels(image).ToList();
            var bg = new Rgba32(Median(border.Select(p => p.R)), Median(border.Select(p => p.G)), Median(border.Select(p => p.B)));
            var spread = Math.Sqrt(border.Average(p => Distance2(p, bg)));
            var tolerance = Math.Clamp(28 + spread * 1.5, 28, 70);
            var limit = tolerance * tolerance;

            // 周囲から、背景色に近い画素をたどって背景とする（被写体の内側の同じ色は残る）
            var queue = new Queue<int>();
            for (var x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
            for (var y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                int x = i % w, y = i / w;
                if (x > 0) Seed(x - 1, y);
                if (x < w - 1) Seed(x + 1, y);
                if (y > 0) Seed(x, y - 1);
                if (y < h - 1) Seed(x, y + 1);
            }

            void Seed(int x, int y)
            {
                var i = y * w + x;
                if (background[i] || Distance2(image[x, y], bg) > limit) return;
                background[i] = true;
                queue.Enqueue(i);
            }
        }

        var foreground = 0;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (background[y * w + x]) continue;
                foreground++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        var borderTotal = 2 * (w + h) - 4;
        var borderBackground = 0;
        for (var x = 0; x < w; x++) { if (background[x]) borderBackground++; if (background[(h - 1) * w + x]) borderBackground++; }
        for (var y = 1; y < h - 1; y++) { if (background[y * w]) borderBackground++; if (background[y * w + w - 1]) borderBackground++; }
        var ratio = (double)foreground / (w * h);
        var confidence = ratio is >= 0.02 and <= 0.95 ? (double)borderBackground / borderTotal : 0;

        // 背景を透明にし、境界の1画素は半透明にしてなじませる
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var p = image[x, y];
                if (background[i])
                {
                    image[x, y] = new Rgba32(p.R, p.G, p.B, 0);
                }
                else if ((x > 0 && background[i - 1]) || (x < w - 1 && background[i + 1])
                         || (y > 0 && background[i - w]) || (y < h - 1 && background[i + w]))
                {
                    image[x, y] = new Rgba32(p.R, p.G, p.B, (byte)Math.Min(p.A, (byte)200));
                }
            }
        }
        StripMetadata(image);
        var bounds = maxX < 0
            ? new NormalizedRect(0, 0, 0, 0)
            : new NormalizedRect((double)minX / w, (double)minY / h, (double)(maxX - minX + 1) / w, (double)(maxY - minY + 1) / h);
        return new Cutout(await PngAsync(image, ct), bounds, confidence);
    }

    public async Task<ProcessedImage> CompositeAsync(byte[] background, byte[] cutout, ProductPlacement placement, double scale,
        CancellationToken ct)
    {
        using var canvas = Image.Load<Rgba32>(s_decoder, background);
        using var product = Image.Load<Rgba32>(s_decoder, cutout);
        var bounds = OpaqueBounds(product);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("切り抜いた商品が空です。");
        product.Mutate(x => x.Crop(bounds));

        // 背景に対する大きさ（高さ基準、幅は 90% まで）。拡大はしない（商品の画質を保つ）
        scale = Math.Clamp(scale, 0.2, 0.9);
        var factor = Math.Min(scale * canvas.Height / product.Height, 0.9 * canvas.Width / product.Width);
        factor = Math.Min(factor, 1.0);
        if (factor < 1.0) product.Mutate(x => x.Resize(Math.Max(1, (int)(product.Width * factor)), Math.Max(1, (int)(product.Height * factor))));

        var margin = (int)(canvas.Height * 0.06);
        var x0 = placement switch
        {
            ProductPlacement.Left => (int)(canvas.Width * 0.08),
            ProductPlacement.Right => canvas.Width - product.Width - (int)(canvas.Width * 0.08),
            _ => (canvas.Width - product.Width) / 2,
        };
        var y0 = placement == ProductPlacement.Center ? (canvas.Height - product.Height) / 2 : canvas.Height - product.Height - margin;

        // 接地の影（ぼかした楕円）
        using (var shadow = new Image<Rgba32>(canvas.Width, canvas.Height, Color.Transparent.ToPixel<Rgba32>()))
        {
            var ellipse = new EllipsePolygon(x0 + product.Width / 2f, y0 + product.Height, product.Width * 0.45f, Math.Max(6, product.Height * 0.04f));
            shadow.Mutate(s => s.Fill(Color.Black.WithAlpha(0.35f), ellipse).GaussianBlur(Math.Max(4, product.Width / 30f)));
            canvas.Mutate(c => c.DrawImage(shadow, 1f));
        }
        canvas.Mutate(c => c.DrawImage(product, new Point(x0, y0), 1f));
        StripMetadata(canvas);
        return await PngAsync(canvas, ct);
    }

    private static Rectangle ToPixels(NormalizedRect r, int width, int height)
    {
        var x = (int)Math.Floor(Math.Clamp(r.X, 0, 1) * width);
        var y = (int)Math.Floor(Math.Clamp(r.Y, 0, 1) * height);
        var right = (int)Math.Ceiling(Math.Clamp(r.X + r.Width, 0, 1) * width);
        var bottom = (int)Math.Ceiling(Math.Clamp(r.Y + r.Height, 0, 1) * height);
        return new Rectangle(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    /// <summary>範囲の内側ほど 1、外側は 0。境界から <see cref="BlendBand"/> 画素で滑らかに変える。</summary>
    private static float Inside(Rectangle r, int x, int y)
    {
        if (!r.Contains(x, y)) return 0;
        var d = Math.Min(Math.Min(x - r.Left, r.Right - 1 - x), Math.Min(y - r.Top, r.Bottom - 1 - y));
        return Math.Min(1f, (d + 1f) / BlendBand);
    }

    private static Rgba32 Blend(Rgba32 original, Rgba32 edited, float t) => new(
        (byte)(original.R + (edited.R - original.R) * t), (byte)(original.G + (edited.G - original.G) * t),
        (byte)(original.B + (edited.B - original.B) * t), (byte)(original.A + (edited.A - original.A) * t));

    private static double Distance2(Rgba32 a, Rgba32 b) =>
        (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

    private static byte Median(IEnumerable<byte> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? (byte)0 : sorted[sorted.Length / 2];
    }

    private static IEnumerable<Rgba32> BorderPixels(Image<Rgba32> image)
    {
        for (var x = 0; x < image.Width; x++) { yield return image[x, 0]; yield return image[x, image.Height - 1]; }
        for (var y = 1; y < image.Height - 1; y++) { yield return image[0, y]; yield return image[image.Width - 1, y]; }
    }

    private static double BorderTransparency(Image<Rgba32> image)
    {
        var border = BorderPixels(image).ToList();
        return border.Count == 0 ? 0 : (double)border.Count(p => p.A < 16) / border.Count;
    }

    private static Rectangle OpaqueBounds(Image<Rgba32> image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image[x, y].A < 16) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
