using ReachForge.Application.Abstractions;
using SkiaSharp;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// 広告ポスターの組版。
/// <list type="bullet">
/// <item>写真：全面に敷き、文字を置く側（写真の中で模様の少ない側：縦長・正方形は上か下、横長は左か右）に、ブランドの濃い色のグラデーションをかける</item>
/// <item>画面：背景の上で、文字と反対側に端末の枠に入れて置く（縦長は文字が上・端末が下、横長は文字が左・端末が右）</item>
/// <item>文字だけ：背景の中央に大きく組む</item>
/// </list>
/// 文字は「小見出し（アクセントの線つき）→ 見出し（太いフォント・詰めた行間・句読点で改行）→ 補足 → ボタン」の順に、四辺の余白をそろえて置き、
/// いちばん下にブランド名と URL を入れる。
/// </summary>
public sealed class SkiaPosterRenderer(SkiaImageProcessor images) : IPosterRenderer
{
    private enum Side { Top, Bottom, Left, Right, Center }

    public Task<ProcessedImage> RenderAsync(PosterSpec spec, (int Width, int Height) size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (w, h) = size;
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            Draw(canvas, spec, w, h);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 93);
        return Task.FromResult(new ProcessedImage(data.ToArray(), "image/jpeg", w, h));
    }

    private void Draw(SKCanvas canvas, PosterSpec spec, int w, int h)
    {
        var landscape = (double)w / h > 1.15;
        var min = Math.Min(w, h);
        var margin = min * 0.07f;
        var (deepF, accentF, highlightF) = SkiaImageProcessor.Palette(spec.Style.Colors);
        var deep = Color(deepF);
        var accent = Vivid(accentF);
        var highlight = Vivid(highlightF);

        // ---- 背景 ----
        Side side;
        if (spec.Photo is { } photo)
        {
            using var cover = SkiaImageProcessor.Cover(photo, w, h);
            canvas.DrawBitmap(cover, 0, 0, SKSamplingOptions.Default);
            side = landscape ? CalmerSide(cover, Side.Left, Side.Right) : CalmerSide(cover, Side.Bottom, Side.Top);
            Scrim(canvas, side, w, h, deep);
        }
        else
        {
            using var background = spec.Backdrop is { } image ? SkiaImageProcessor.Cover(image, w, h) : SkiaImageProcessor.Backdrop(w, h, spec.Style);
            canvas.DrawBitmap(background, 0, 0, SKSamplingOptions.Default);
            side = spec.Screen is null ? Side.Center : landscape ? Side.Left : Side.Top;
            if (spec.Screen is not null) Scrim(canvas, side, w, h, deep, 0.55f);
        }

        var footerHeight = string.IsNullOrWhiteSpace(spec.Brand) && string.IsNullOrWhiteSpace(spec.Footer) ? 0 : min * 0.06f;
        var bottomLimit = h - margin - h * Math.Clamp(spec.SafeBottom, 0, 0.4f) - footerHeight;

        // ---- 文字の置き場所 ----
        SKRect zone = side switch
        {
            Side.Left => SKRect.Create(margin, margin, w * 0.46f - margin, bottomLimit - margin),
            Side.Right => SKRect.Create(w * 0.54f, margin, w * 0.46f - margin, bottomLimit - margin),
            Side.Top => SKRect.Create(margin, margin, w - margin * 2, (bottomLimit - margin) * (spec.Screen is null ? 0.6f : 0.42f)),
            Side.Bottom => SKRect.Create(margin, margin + (bottomLimit - margin) * 0.4f, w - margin * 2, (bottomLimit - margin) * 0.6f),
            _ => SKRect.Create(margin * 1.2f, margin, w - margin * 2.4f, bottomLimit - margin),
        };

        // 画面：文字と反対側に端末を置く
        if (spec.Screen is { } screen)
        {
            var area = landscape
                ? SKRect.Create(w * 0.46f, margin * 0.6f, w * 0.54f - margin * 0.4f, bottomLimit - margin * 0.6f)
                : SKRect.Create(margin * 0.5f, zone.Bottom, w - margin, bottomLimit - zone.Bottom);
            DrawDevice(canvas, screen, area);
        }

        // ---- 文字（下から積む場所では、下にそろえる） ----
        var center = side == Side.Center;
        var stack = Measure(spec, zone.Width, min, center);
        var top = side switch
        {
            Side.Bottom => zone.Bottom - stack.Height,
            Side.Center => zone.Top + (zone.Height - stack.Height) / 2,
            Side.Left or Side.Right when spec.Screen is null => zone.Top + (zone.Height - stack.Height) / 2,
            Side.Left or Side.Right => zone.Top + (zone.Height - stack.Height) / 2,
            _ => zone.Top,
        };
        DrawStack(canvas, stack, zone.Left, zone.Width, Math.Max(zone.Top, top), center, accent, highlight);

        // ---- いちばん下：ブランド名と URL ----
        if (footerHeight > 0) DrawFooter(canvas, spec, margin, w, bottomLimit + footerHeight, min, center);
    }

    // ---- 背景の処理 ----

    private static SKColor Color((float R, float G, float B) c) =>
        new((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255));

    private static SKColor Vivid((float R, float G, float B) c)
    {
        Color(c).ToHsv(out var hue, out var sat, out _);
        return SKColor.FromHsv(hue, Math.Max(sat, 65), 100);
    }

    /// <summary>写真の2つの側のうち、模様（輪郭）の少ない方。文字を置いても読みやすい。</summary>
    private static Side CalmerSide(SKBitmap image, Side a, Side b)
    {
        const int Size = 64;
        using var small = SkiaImageProcessor.Resize(image, Size, Size);
        double Energy(Side side)
        {
            double sum = 0;
            for (var y = 1; y < Size - 1; y++)
            {
                for (var x = 1; x < Size - 1; x++)
                {
                    var inside = side switch
                    {
                        Side.Top => y < Size * 0.4,
                        Side.Bottom => y > Size * 0.6,
                        Side.Left => x < Size * 0.45,
                        _ => x > Size * 0.55,
                    };
                    if (!inside) continue;
                    double L(int px, int py)
                    {
                        var c = small.GetPixel(px, py);
                        return 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;
                    }
                    sum += Math.Abs(L(x + 1, y) - L(x - 1, y)) + Math.Abs(L(x, y + 1) - L(x, y - 1));
                }
            }
            return sum;
        }
        // 同じくらいなら、広告の定番（下・左）にする
        return Energy(a) <= Energy(b) * 1.1 ? a : b;
    }

    /// <summary>文字を置く側に、ブランドの濃い色のグラデーションをかける（写真の上でも文字が読める）。</summary>
    private static void Scrim(SKCanvas canvas, Side side, int w, int h, SKColor deep, float strength = 0.9f)
    {
        var (from, to) = side switch
        {
            Side.Top => (new SKPoint(0, 0), new SKPoint(0, h * 0.62f)),
            Side.Bottom => (new SKPoint(0, h), new SKPoint(0, h * 0.38f)),
            Side.Left => (new SKPoint(0, 0), new SKPoint(w * 0.62f, 0)),
            Side.Right => (new SKPoint(w, 0), new SKPoint(w * 0.38f, 0)),
            _ => (new SKPoint(0, h), new SKPoint(0, 0)),
        };
        var dark = new SKColor((byte)(deep.Red * 0.6), (byte)(deep.Green * 0.6), (byte)(deep.Blue * 0.6));
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(from, to,
                [dark.WithAlpha((byte)(255 * strength)), dark.WithAlpha((byte)(170 * strength)), dark.WithAlpha(0)], [0, 0.45f, 1], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, w, h, paint);
    }

    /// <summary>端末の枠に入れた画面を、指定の範囲いっぱいに置く。</summary>
    private static void DrawDevice(SKCanvas canvas, byte[] screen, SKRect area)
    {
        if (area.Width < 10 || area.Height < 10) return;
        using var shot = SkiaImageProcessor.Decode(screen);
        using var layer = new SKBitmap(new SKImageInfo((int)area.Width, (int)area.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        layer.Erase(SKColors.Transparent);
        SkiaImageProcessor.DrawDevice(layer, shot, 0);
        canvas.DrawBitmap(layer, area.Left, area.Top, SKSamplingOptions.Default);
    }

    // ---- 文字 ----

    private sealed record Block(SKFont Font, List<string> Lines, float LineHeight, SKColor Color, float GapAfter);

    private sealed record Stack(Block? Kicker, Block Headline, Block? Sub, (SKFont Font, string Text, float Height)? Button, float Height) : IDisposable
    {
        public void Dispose()
        {
            Kicker?.Font.Dispose();
            Headline.Font.Dispose();
            Sub?.Font.Dispose();
            Button?.Font.Dispose();
        }
    }

    private Stack Measure(PosterSpec spec, float width, float min, bool center)
    {
        var display = images.DisplayTypeface;
        var text = images.Typeface;
        Block? kicker = null;
        if (!string.IsNullOrWhiteSpace(spec.Kicker))
        {
            var (font, lines) = Fit(text, spec.Kicker!, width * 0.9f, min * 0.032f, min * 0.022f, 1, false);
            kicker = new Block(font, lines, font.Size * 1.3f, SKColors.White, min * 0.022f);
        }
        // 見出し：大きく、行間を詰めて、句読点で改行する（3行まで。収まらなければ小さくする）
        var (hFont, hLines) = Fit(display, spec.Headline, width, min * (center ? 0.12f : 0.105f), min * 0.05f, 3, true);
        var headline = new Block(hFont, hLines, hFont.Size * 1.16f, SKColors.White, min * 0.03f);
        Block? sub = null;
        if (!string.IsNullOrWhiteSpace(spec.Sub))
        {
            var (font, lines) = Fit(text, spec.Sub!, width * (center ? 0.92f : 1f), min * 0.036f, min * 0.026f, 2, true);
            sub = new Block(font, lines, font.Size * 1.45f, new SKColor(0xFF, 0xFF, 0xFF, 225), min * 0.036f);
        }
        (SKFont, string, float)? button = null;
        if (!string.IsNullOrWhiteSpace(spec.Button))
        {
            var font = SkiaImageProcessor.CreateFont(text, min * 0.034f);
            button = (font, spec.Button!, font.Size * 2.4f);
        }
        var height = (kicker is null ? 0 : kicker.LineHeight + kicker.GapAfter)
                     + headline.Lines.Count * headline.LineHeight + headline.GapAfter
                     + (sub is null ? 0 : sub.Lines.Count * sub.LineHeight + sub.GapAfter)
                     + (button?.Item3 ?? 0);
        return new Stack(kicker, headline, sub, button, height);
    }

    /// <summary>幅と行数に収まる大きさ（大きい方から）。<paramref name="phrase"/> なら、句読点・空白の位置で改行を試す。</summary>
    private static (SKFont Font, List<string> Lines) Fit(SKTypeface typeface, string value, float maxWidth, float start, float min, int maxLines,
        bool phrase)
    {
        // まず意味のまとまり（句読点・空白）で改行できる大きさを探し、なければ1文字単位で折り返す
        foreach (var byPhrase in phrase ? new[] { true, false } : [false])
        {
            for (var size = start; size >= min; size *= 0.95f)
            {
                var font = SkiaImageProcessor.CreateFont(typeface, size);
                var lines = byPhrase ? PhraseWrap(value, font, maxWidth) : SkiaImageProcessor.Wrap(value, font, maxWidth);
                if (lines is not null && lines.Count <= maxLines) return (font, lines);
                font.Dispose();
            }
        }
        var smallest = SkiaImageProcessor.CreateFont(typeface, min);
        return (smallest, SkiaImageProcessor.Wrap(value, smallest, maxWidth));
    }

    /// <summary>
    /// 句読点・空白（、。！？・ と空白）の後ろで区切り、各行が幅に収まるように組む（日本語の見出しを意味のまとまりで改行する）。
    /// 区切りがない・収まらない場合は null（1文字単位の折り返しにまかせる）。
    /// </summary>
    internal static List<string>? PhraseWrap(string text, SKFont font, float maxWidth)
    {
        var value = text.Trim();
        if (font.MeasureText(value) <= maxWidth) return [value];
        var pieces = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if ("、。！？!?・ 　".Contains(value[i]) && i + 1 < value.Length)
            {
                pieces.Add(value[start..(i + 1)]);
                start = i + 1;
            }
        }
        pieces.Add(value[start..]);
        if (pieces.Count < 2) return null;
        var lines = new List<string>();
        var current = "";
        foreach (var piece in pieces)
        {
            if (current.Length > 0 && font.MeasureText(current + piece) > maxWidth)
            {
                lines.Add(current.Trim());
                current = "";
            }
            current += piece;
        }
        lines.Add(current.Trim());
        return lines.All(l => font.MeasureText(l) <= maxWidth) ? lines : null;
    }

    private static void DrawStack(SKCanvas canvas, Stack stack, float left, float width, float top, bool center, SKColor accent, SKColor highlight)
    {
        using (stack)
        {
            var x = center ? left + width / 2 : left;
            var align = center ? SKTextAlign.Center : SKTextAlign.Left;
            var y = top;
            if (stack.Kicker is { } kicker)
            {
                // 小見出し：アクセントの色の短い線と、少し字間をあけた文字
                var barWidth = kicker.Font.Size * 1.6f;
                var textWidth = kicker.Font.MeasureText(kicker.Lines[0]);
                var barX = center ? x - (barWidth + kicker.Font.Size * 0.6f + textWidth) / 2 : x;
                using (var bar = new SKPaint { Color = accent, IsAntialias = true })
                {
                    canvas.DrawRoundRect(SKRect.Create(barX, y + kicker.LineHeight / 2 - kicker.Font.Size * 0.08f, barWidth, kicker.Font.Size * 0.16f),
                        2, 2, bar);
                }
                using var paint = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 235), IsAntialias = true };
                canvas.DrawText(kicker.Lines[0], barX + barWidth + kicker.Font.Size * 0.6f, y - kicker.Font.Metrics.Ascent * 0.95f, SKTextAlign.Left,
                    kicker.Font, paint);
                y += kicker.LineHeight + kicker.GapAfter;
            }
            // 見出し：白い太字に、やわらかな影
            using (var shadow = new SKPaint
                   {
                       IsAntialias = true, Color = SKColors.Black.WithAlpha(110),
                       ImageFilter = SKImageFilter.CreateBlur(stack.Headline.Font.Size * 0.12f, stack.Headline.Font.Size * 0.12f),
                   })
            using (var paint = new SKPaint { IsAntialias = true, Color = stack.Headline.Color })
            {
                foreach (var line in stack.Headline.Lines)
                {
                    var baseline = y - stack.Headline.Font.Metrics.Ascent * 0.92f;
                    canvas.DrawText(line, x, baseline + stack.Headline.Font.Size * 0.04f, align, stack.Headline.Font, shadow);
                    canvas.DrawText(line, x, baseline, align, stack.Headline.Font, paint);
                    y += stack.Headline.LineHeight;
                }
            }
            y += stack.Headline.GapAfter;
            if (stack.Sub is { } sub)
            {
                using var paint = new SKPaint { IsAntialias = true, Color = sub.Color };
                foreach (var line in sub.Lines)
                {
                    canvas.DrawText(line, x, y - sub.Font.Metrics.Ascent, align, sub.Font, paint);
                    y += sub.LineHeight;
                }
                y += sub.GapAfter;
            }
            if (stack.Button is { } button)
            {
                var bw = button.Font.MeasureText(button.Text) + button.Font.Size * 2.6f;
                var bh = button.Height * 0.78f;
                var bx = center ? x - bw / 2 : x;
                var rect = SKRect.Create(bx, y, bw, bh);
                using (var glow = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, bh * 0.35f) })
                {
                    canvas.DrawRoundRect(rect, bh / 2, bh / 2, glow);
                }
                using (var fill = new SKPaint
                       {
                           IsAntialias = true,
                           Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom), [accent, highlight],
                               SKShaderTileMode.Clamp),
                       })
                {
                    canvas.DrawRoundRect(rect, bh / 2, bh / 2, fill);
                }
                using var label = new SKPaint { IsAntialias = true, Color = SKColors.White };
                canvas.DrawText(button.Text + "  →", rect.MidX, rect.MidY - (button.Font.Metrics.Ascent + button.Font.Metrics.Descent) / 2,
                    SKTextAlign.Center, button.Font, label);
            }
        }
    }

    private void DrawFooter(SKCanvas canvas, PosterSpec spec, float margin, int w, float baseline, float min, bool center)
    {
        using var brandFont = SkiaImageProcessor.CreateFont(images.Typeface, min * 0.03f);
        using var urlFont = SkiaImageProcessor.CreateFont(images.Typeface, min * 0.022f);
        using var brandPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var urlPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xFF, 0xFF, 190) };
        var brand = spec.Brand?.Trim() ?? "";
        var url = spec.Footer?.Trim() ?? "";
        if (center)
        {
            // 中央：ブランド名と URL を1行に並べて中央にそろえる
            var gap = min * 0.03f;
            var brandWidth = brand.Length > 0 ? brandFont.MeasureText(brand) : 0;
            var urlWidth = url.Length > 0 ? urlFont.MeasureText(url) : 0;
            var start = w / 2f - (brandWidth + (brandWidth > 0 && urlWidth > 0 ? gap : 0) + urlWidth) / 2;
            if (brand.Length > 0) canvas.DrawText(brand, start, baseline, SKTextAlign.Left, brandFont, brandPaint);
            if (url.Length > 0) canvas.DrawText(url, start + brandWidth + (brandWidth > 0 ? gap : 0), baseline, SKTextAlign.Left, urlFont, urlPaint);
            return;
        }
        if (brand.Length > 0) canvas.DrawText(brand, margin, baseline, SKTextAlign.Left, brandFont, brandPaint);
        if (url.Length > 0) canvas.DrawText(url, w - margin, baseline, SKTextAlign.Right, urlFont, urlPaint);
    }
}
