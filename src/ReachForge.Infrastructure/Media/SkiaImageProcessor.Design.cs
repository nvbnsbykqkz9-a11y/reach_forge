using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using SkiaSharp;

namespace ReachForge.Infrastructure.Media;

/// <summary>広告のデザイン：LP の色に合わせた背景の模様と、サービスの画面を入れる端末の枠。</summary>
public sealed partial class SkiaImageProcessor
{
    /// <summary>背景の模様を計算する解像度（出力の 1/4。模様はなめらかなので、拡大しても粗くならない）。</summary>
    private const int BackdropScale = 4;

    public async Task<ProcessedImage> RenderBackdropAsync((int Width, int Height) size, BackdropStyle style, CancellationToken ct)
    {
        using var image = Backdrop(size.Width, size.Height, style);
        return await JpegAsync(image, 92, ct);
    }

    /// <summary>
    /// 背景を描く：濃い地色に、模様（煙・光・オーロラ・光の粒・波）を色（パレット）で重ね、四隅を少し暗くして、細かな粒子を加える。
    /// 模様は値ノイズ（fBm）で描き、<see cref="BackdropStyle.Time"/> で少しずつ流れる。
    /// </summary>
    internal static SKBitmap Backdrop(int width, int height, BackdropStyle style)
    {
        var (deep, accent, highlight) = Palette(style.Colors);
        var lw = Math.Max(8, width / BackdropScale);
        var lh = Math.Max(8, height / BackdropScale);
        var t = style.Time;
        var random = new Random(style.Seed);
        var noise = new ValueNoise(style.Seed);
        var particles = Enumerable.Range(0, style.Motif == BackdropMotif.Bokeh ? 60 : 36)
            .Select(_ => (X: random.NextSingle() * lw, Y: random.NextSingle() * lh, R: 1.5f + random.NextSingle() * lw / 40f,
                Speed: 2 + random.NextSingle() * 6, K: random.NextSingle()))
            .ToList();
        var scale = 3.2f / Math.Min(lw, lh);
        var center = (X: lw * 0.5f, Y: lh * 0.42f);

        var pixels = new Rgba(lw, lh);
        var buffer = new (float R, float G, float B)[lw * lh];
        for (var y = 0; y < lh; y++)
        {
            for (var x = 0; x < lw; x++)
            {
                var n = noise.Fbm(x * scale + t * 0.05f, y * scale + t * 0.08f) * 0.65f + noise.Fbm(x * scale * 1.7f - t * 0.06f + 9, y * scale * 1.7f + 4) * 0.35f;
                var v = y / (float)lh;
                var c = Mix((0, 0, 0), deep, 1.0f - v * 0.25f);
                switch (style.Motif)
                {
                    case BackdropMotif.Smoke:
                        c = Add(c, accent, Smooth(0.35f, 0.85f, n) * 0.85f);
                        c = Add(c, highlight, Smooth(0.62f, 0.95f, n) * 0.3f);
                        c = Add(c, accent, MathF.Exp(-MathF.Pow((v - 1.05f) / 0.35f, 2)) * 0.5f);
                        break;
                    case BackdropMotif.Rays:
                    {
                        c = Add(c, accent, Smooth(0.4f, 0.9f, n) * 0.6f);
                        var dx = x - center.X;
                        var dy = y - center.Y;
                        var angle = MathF.Atan2(dy, dx);
                        var r = MathF.Sqrt(dx * dx + dy * dy) / Math.Min(lw, lh) + 0.01f;
                        var rays = MathF.Pow(0.5f + 0.5f * MathF.Sin(angle * 11 + t * 0.4f), 6) * 0.5f + MathF.Pow(0.5f + 0.5f * MathF.Sin(angle * 17 - t * 0.3f + 1), 8) * 0.4f;
                        c = Add(c, highlight, (rays * MathF.Exp(-r / 0.6f) + MathF.Exp(-r / 0.13f)) * 0.45f);
                        break;
                    }
                    case BackdropMotif.Bokeh:
                        c = Add(c, accent, Smooth(0.3f, 0.95f, n) * 0.5f);
                        break;
                    case BackdropMotif.Waves:
                    {
                        var wave1 = MathF.Exp(-MathF.Pow((v - 0.35f - MathF.Sin(x / (float)lw * 5 + t * 0.4f) * 0.06f - (n - 0.5f) * 0.1f) / 0.14f, 2));
                        var wave2 = MathF.Exp(-MathF.Pow((v - 0.7f - MathF.Sin(x / (float)lw * 4 - t * 0.3f + 2) * 0.07f) / 0.18f, 2));
                        c = Add(c, accent, wave1 * 0.55f + Smooth(0.45f, 0.95f, n) * 0.25f);
                        c = Add(c, highlight, wave2 * 0.35f);
                        break;
                    }
                    default: // Aurora
                    {
                        var band1 = MathF.Exp(-MathF.Pow((v - 0.3f - MathF.Sin(x / (float)lw * 6 + t * 0.6f) * 0.05f - (n - 0.5f) * 0.17f) / 0.12f, 2));
                        var band2 = MathF.Exp(-MathF.Pow((v - 0.56f - MathF.Sin(x / (float)lw * 5 - t * 0.5f + 2) * 0.06f - (n - 0.5f) * 0.19f) / 0.1f, 2));
                        c = Add(c, accent, band1 * 0.6f + Smooth(0.5f, 0.95f, n) * 0.3f);
                        c = Add(c, highlight, band2 * 0.5f);
                        break;
                    }
                }
                buffer[y * lw + x] = c;
            }
        }

        // 光の粒（ぼけた円。ゆっくり上へ流れる）
        foreach (var p in particles)
        {
            var py = ((p.Y - t * p.Speed) % lh + lh) % lh;
            var px = p.X + MathF.Sin(t * 0.5f + p.K * 6) * 4;
            var strength = (style.Motif == BackdropMotif.Bokeh ? 0.55f : 0.3f) * (0.4f + 0.6f * p.K);
            var color = Mix(accent, highlight, p.K);
            for (var y = (int)Math.Max(0, py - 2 * p.R); y < Math.Min(lh, py + 2 * p.R); y++)
            {
                for (var x = (int)Math.Max(0, px - 2 * p.R); x < Math.Min(lw, px + 2 * p.R); x++)
                {
                    var d = ((x - px) * (x - px) + (y - py) * (y - py)) / (p.R * p.R);
                    var disc = MathF.Pow(Math.Clamp(1.3f - d, 0, 1), 1.5f) * strength;
                    if (disc > 0) buffer[y * lw + x] = Add(buffer[y * lw + x], color, disc);
                }
            }
        }

        // 四隅を暗く（ビネット）し、やわらかく明るさをそろえる（トーンマップ）
        for (var y = 0; y < lh; y++)
        {
            for (var x = 0; x < lw; x++)
            {
                var vx = (x - lw / 2f) / (lw * 0.75f);
                var vy = (y - lh * 0.48f) / (lh * 0.72f);
                var vignette = Math.Clamp(1.15f - 0.9f * (vx * vx + vy * vy), 0.25f, 1f);
                var (r, g, b) = buffer[y * lw + x];
                pixels[x, y] = new SKColor(Tone(r * vignette), Tone(g * vignette), Tone(b * vignette));
            }
        }

        using var small = pixels.ToBitmap();
        var result = Resize(small, width, height);
        // 細かな粒子（フィルムのような質感。拡大による平坦さとバンディングを防ぐ）
        var grain = new Random(style.Seed ^ (int)(t * 30));
        var bytes = result.GetPixelSpan().ToArray();
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var delta = grain.Next(-5, 6);
            bytes[i] = (byte)Math.Clamp(bytes[i] + delta, 0, 255);
            bytes[i + 1] = (byte)Math.Clamp(bytes[i + 1] + delta, 0, 255);
            bytes[i + 2] = (byte)Math.Clamp(bytes[i + 2] + delta, 0, 255);
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length);
        return result;

        static byte Tone(float value) => (byte)Math.Clamp((1 - MathF.Exp(-value * 1.6f)) * 255, 0, 255);
    }

    /// <summary>
    /// 背景の色：地色（とても濃く）・模様の色・光の色。地色は 1 色目を暗くしたもの、模様は彩度のある色、光は明るい色を選ぶ。
    /// </summary>
    internal static ((float R, float G, float B) Deep, (float R, float G, float B) Accent, (float R, float G, float B) Highlight) Palette(
        IReadOnlyList<string> colorsHex)
    {
        var colors = colorsHex.Select(h => SKColor.TryParse(h, out var c) ? c : SKColor.Empty).Where(c => c != SKColor.Empty).ToList();
        if (colors.Count == 0) colors.Add(new SKColor(0x1F, 0x4F, 0xD8));
        static (float, float, float) F(SKColor c) => (c.Red / 255f, c.Green / 255f, c.Blue / 255f);
        static SKColor Hsv(SKColor c, float? s = null, float? v = null)
        {
            c.ToHsv(out var h, out var sat, out var val);
            return SKColor.FromHsv(h, s ?? sat, v ?? val);
        }
        // 模様の色：いちばん鮮やかな色（灰色ばかりならそのまま）
        var vivid = colors.MaxBy(c => { c.ToHsv(out _, out var sat, out var val); return sat * val; });
        vivid.ToHsv(out var vh, out var vs, out _);
        var accent = Hsv(vivid, Math.Max(vs, 55), 78);
        var deep = Hsv(colors[0] == vivid && colors.Count > 1 ? colors[1] : colors[0], null, 14);
        deep.ToHsv(out _, out var ds, out _);
        if (ds < 20) deep = SKColor.FromHsv(vh, 45, 12);
        var other = colors.FirstOrDefault(c => c != vivid && c != colors[0]);
        var highlight = other == default ? SKColor.FromHsv((vh + 40) % 360, Math.Max(vs, 50), 100) : Hsv(other, null, 100);
        return (F(deep), F(accent), F(highlight));
    }

    private static (float R, float G, float B) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static (float R, float G, float B) Add((float R, float G, float B) a, (float R, float G, float B) b, float k) =>
        (a.R + b.R * k, a.G + b.G * k, a.B + b.B * k);

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>値ノイズ（格子点の乱数をなめらかに補間）と、その重ね合わせ（fBm、0〜1）。</summary>
    private sealed class ValueNoise(int seed)
    {
        private float Lattice(int x, int y)
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }

        private float Noise(float x, float y)
        {
            var xi = (int)MathF.Floor(x);
            var yi = (int)MathF.Floor(y);
            var xf = x - xi;
            var yf = y - yi;
            var u = xf * xf * (3 - 2 * xf);
            var v = yf * yf * (3 - 2 * yf);
            var a = Lattice(xi, yi) + (Lattice(xi + 1, yi) - Lattice(xi, yi)) * u;
            var b = Lattice(xi, yi + 1) + (Lattice(xi + 1, yi + 1) - Lattice(xi, yi + 1)) * u;
            return a + (b - a) * v;
        }

        public float Fbm(float x, float y)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (var octave = 0; octave < 5; octave++)
            {
                sum += Noise(x, y) * amplitude;
                total += amplitude;
                x = x * 2.03f + 17;
                y = y * 2.03f + 31;
                amplitude *= 0.5f;
            }
            return sum / total;
        }
    }

    public async Task<ProcessedImage> ComposeDeviceAsync(byte[]? background, byte[] screen, (int Width, int Height) size, TextOverlay? caption,
        CancellationToken ct)
    {
        var (w, h) = size;
        using var shot = Decode(screen);
        using var canvasBitmap = background is null ? NewBitmap(w, h, SKColors.Transparent) : Cover(background, w, h);
        DrawDevice(canvasBitmap, shot, ReservedBottom(w, h, caption));
        if (caption is not null) DrawOverlay(canvasBitmap, caption);
        return background is null ? await PngAsync(canvasBitmap, ct) : await JpegAsync(canvasBitmap, 92, ct);
    }

    /// <summary>下にあける割合：見出し・テロップの帯（約 14%）と、縦型でアプリの表示に隠れる部分（<see cref="TextOverlay.SafeBottom"/>）。</summary>
    internal static float ReservedBottom(int width, int height, TextOverlay? caption)
    {
        var safe = caption?.SafeBottom ?? ((double)width / height < 0.7 ? TextOverlay.VerticalSafeBottom : 0);
        return 0.15f + Math.Clamp(safe, 0, 0.4f);
    }

    /// <summary>枠を覆う大きさにして中央を切り出す。</summary>
    internal static SKBitmap Cover(byte[] source, int width, int height)
    {
        using var image = Decode(source);
        var cover = Math.Max((double)width / image.Width, (double)height / image.Height);
        using var resized = Resize(image, Math.Max(width, (int)Math.Ceiling(image.Width * cover)), Math.Max(height, (int)Math.Ceiling(image.Height * cover)));
        return Crop(resized, SKRectI.Create((resized.Width - width) / 2, (resized.Height - height) / 2, width, height));
    }

    /// <summary>
    /// 端末の枠を描き、画面を入れる。横長の画面（幅 ≧ 高さ）はノートパソコン、縦長はスマートフォン（9:19.5）。
    /// 画面は枠いっぱいに拡大して上から合わせる（縦に長いページは上の部分を見せる）。
    /// </summary>
    internal static SKRect DrawDevice(SKBitmap target, SKBitmap shot, float reservedBottom)
    {
        var w = target.Width;
        var h = target.Height;
        var top = h * 0.07f;
        var bottom = h * (1 - reservedBottom) - h * 0.02f;
        var areaW = w * 0.86f;
        var areaH = Math.Max(h * 0.2f, bottom - top);
        var laptop = shot.Width >= shot.Height;
        using var canvas = new SKCanvas(target);

        SKRect body, screenRect;
        float radius;
        if (laptop)
        {
            // 画面の比率（16:10 前後に収める）・ふた（画面のまわりの枠）・本体（ふたより少し広い台）
            var screenRatio = Math.Clamp((float)shot.Width / shot.Height, 1.3f, 1.9f);
            const float bezel = 0.035f;
            const float baseHeightRatio = 0.06f;
            // ふたの高さ ＝ 画面の高さ ＋ 上下の枠（下の枠は少し太い）
            float LidHeight(float lw) => lw * (1 - 2 * bezel) / screenRatio + lw * bezel * 2.4f;
            var lidW = areaW / 1.12f;
            if (LidHeight(lidW) * (1 + baseHeightRatio) > areaH) lidW *= areaH / (LidHeight(lidW) * (1 + baseHeightRatio));
            var lidH = LidHeight(lidW);
            var x = (w - lidW) / 2;
            var y = top + (areaH - lidH * (1 + baseHeightRatio)) / 2;
            body = SKRect.Create(x, y, lidW, lidH);
            var b = lidW * bezel;
            screenRect = SKRect.Create(x + b, y + b, lidW - 2 * b, lidH - b * 2.4f);
            radius = lidW * 0.025f;
            Shadow(canvas, SKRect.Create(x - lidW * 0.06f, y, lidW * 1.12f, lidH * (1 + baseHeightRatio)), radius, w);
            using var lid = new SKPaint { Color = new SKColor(0x1C, 0x1E, 0x24), IsAntialias = true };
            canvas.DrawRoundRect(body, radius, radius, lid);
            using var edge = new SKPaint { Color = new SKColor(0x5A, 0x60, 0x6E), IsAntialias = true, IsStroke = true, StrokeWidth = Math.Max(1.5f, lidW / 400) };
            canvas.DrawRoundRect(body, radius, radius, edge);
            // 本体（キーボード側）：ふたより広い薄い台と、中央のくぼみ
            var baseRect = SKRect.Create(x - lidW * 0.06f, y + lidH, lidW * 1.12f, lidH * baseHeightRatio);
            using var deck = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, baseRect.Top), new SKPoint(0, baseRect.Bottom),
                    [new SKColor(0xD5, 0xD9, 0xE0), new SKColor(0x8C, 0x92, 0x9E)], SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(baseRect, baseRect.Height * 0.5f, baseRect.Height * 0.5f, deck);
            using var notch = new SKPaint { Color = new SKColor(0x70, 0x76, 0x82), IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(w / 2f - lidW * 0.08f, baseRect.Top, lidW * 0.16f, baseRect.Height * 0.35f), 4, 4, notch);
        }
        else
        {
            const float phoneRatio = 9f / 19.5f;
            var phoneH = Math.Min(areaH, areaW / phoneRatio);
            var phoneW = phoneH * phoneRatio;
            var x = (w - phoneW) / 2;
            var y = top + (areaH - phoneH) / 2;
            body = SKRect.Create(x, y, phoneW, phoneH);
            radius = phoneW * 0.13f;
            var b = phoneW * 0.035f;
            screenRect = SKRect.Create(x + b, y + b, phoneW - 2 * b, phoneH - 2 * b);
            Shadow(canvas, body, radius, w);
            using var frame = new SKPaint { Color = new SKColor(0x1C, 0x1E, 0x24), IsAntialias = true };
            canvas.DrawRoundRect(body, radius, radius, frame);
            using var edge = new SKPaint { Color = new SKColor(0x6A, 0x70, 0x7E), IsAntialias = true, IsStroke = true, StrokeWidth = Math.Max(1.5f, phoneW / 200) };
            canvas.DrawRoundRect(body, radius, radius, edge);
        }

        // 画面：枠を覆う大きさにして、上から合わせて切り出す（文字は描き直さないので、つぶれない）
        var scale = Math.Max(screenRect.Width / shot.Width, screenRect.Height / shot.Height);
        var srcW = screenRect.Width / scale;
        var srcH = screenRect.Height / scale;
        var src = SKRect.Create((shot.Width - srcW) / 2, 0, srcW, srcH);
        canvas.Save();
        var inner = laptop ? radius * 0.3f : radius * 0.78f;
        using (var clip = new SKRoundRect(screenRect, inner, inner))
        {
            canvas.ClipRoundRect(clip, antialias: true);
        }
        using (var image = SKImage.FromBitmap(shot))
        {
            canvas.DrawImage(image, src, screenRect, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        canvas.Restore();
        if (!laptop)
        {
            // カメラ（画面上部の小さな切り欠き）
            using var island = new SKPaint { Color = new SKColor(0x0A, 0x0A, 0x0C), IsAntialias = true };
            var iw = body.Width * 0.26f;
            var ih = body.Width * 0.065f;
            canvas.DrawRoundRect(SKRect.Create(body.MidX - iw / 2, screenRect.Top + ih * 0.5f, iw, ih), ih / 2, ih / 2, island);
        }
        // 画面のつや（上から斜めのうっすらした光）
        using (var gloss = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(screenRect.Left, screenRect.Top), new SKPoint(screenRect.MidX, screenRect.MidY),
                       [SKColors.White.WithAlpha(28), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp),
               })
        {
            canvas.DrawRect(screenRect, gloss);
        }
        canvas.Flush();
        return screenRect;

        static void Shadow(SKCanvas canvas, SKRect rect, float radius, int width)
        {
            // 背景から浮かせる：下に落ちる影と、周りのうっすらした光
            using var glow = new SKPaint { Color = SKColors.White.WithAlpha(38), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, width / 18f) };
            canvas.DrawRoundRect(rect, radius, radius, glow);
            using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, width / 40f) };
            var offset = rect;
            offset.Offset(0, width / 60f);
            canvas.DrawRoundRect(offset, radius, radius, shadow);
        }
    }
}
