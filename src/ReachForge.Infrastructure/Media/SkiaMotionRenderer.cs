using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Common;
using SkiaSharp;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// モーショングラフィックスの描画。背景（画像、または LP の色の模様）をゆっくり動かし、光の粒を流し、
/// 見出し・カード・チェックリスト・光る円・端末の枠・ボタンを、時間に合わせて動かしながら描く。
/// コマは SkiaSharp で描き、ffmpeg に生の画素として渡して H.264 にする（引数は配列で渡し、シェルを通さない）。
/// </summary>
public sealed class SkiaMotionRenderer(SkiaImageProcessor images, IOptions<VideoOptions> video, ILogger<SkiaMotionRenderer> log) : IMotionRenderer
{
    public const int Fps = 30;

    /// <summary>注意をひく色（課題の提起）。</summary>
    private static readonly SKColor AlertColor = new(0xFF, 0x4D, 0x5E);

    public async Task<byte[]> RenderAsync(MotionScene scene, (int Width, int Height) size, double seconds, CancellationToken ct)
    {
        var (w, h) = size;
        var frames = Math.Max(1, (int)Math.Round(seconds * Fps));
        using var stage = new Stage(scene, w, h, images.Typeface);
        var dir = Directory.CreateTempSubdirectory("rf-motion-");
        try
        {
            var output = Path.Combine(dir.FullName, "scene.mp4");
            var psi = new ProcessStartInfo(video.Value.FfmpegPath)
            {
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{w}x{h}",
                         "-r", Fps.ToString(CultureInfo.InvariantCulture), "-i", "-", "-c:v", "libx264", "-preset", video.Value.Preset,
                         "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output,
                     })
            {
                psi.ArgumentList.Add(a);
            }
            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log.LogError(ex, "ffmpeg was not found at {Path}", video.Value.FfmpegPath);
                throw new DomainException(ErrorCodes.VideoUnavailable, FfmpegVideoComposer.NotFoundMessage);
            }
            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            using var frame = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(frame);
            var input = process.StandardInput.BaseStream;
            try
            {
                for (var i = 0; i < frames; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    stage.Draw(canvas, i / (float)Fps, (float)seconds);
                    canvas.Flush();
                    Write(input, frame);
                }
                await input.FlushAsync(ct);
                input.Close();
            }
            catch (IOException ex)
            {
                // ffmpeg が先に終了した（書き出しの失敗）。理由は下で ffmpeg の出力から知らせる
                log.LogWarning(ex, "ffmpeg closed the input early");
            }
            await process.WaitForExitAsync(ct);
            var error = await stderr + await stdout;
            if (process.ExitCode != 0)
            {
                log.LogError("ffmpeg failed to encode a motion scene ({Code}): {Error}", process.ExitCode, error.Length > 2000 ? error[^2000..] : error);
                throw new DomainException(ErrorCodes.VideoUnavailable, FfmpegVideoComposer.Describe(error));
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Failed to delete temp folder {Path}", dir.FullName);
            }
        }

        static void Write(Stream stream, SKBitmap bitmap) => stream.Write(bitmap.GetPixelSpan());
    }

    public Task<ProcessedImage> RenderStillAsync(MotionScene scene, (int Width, int Height) size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (w, h) = size;
        using var stage = new Stage(scene, w, h, images.Typeface);
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            stage.Draw(canvas, Stage.SettledSeconds, Stage.SettledSeconds + 1, still: true);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
        return Task.FromResult(new ProcessedImage(data.ToArray(), "image/jpeg", w, h));
    }

    /// <summary>1つのシーンの描画（準備した背景・画像を使い、時刻 t のコマを描く）。</summary>
    private sealed class Stage : IDisposable
    {
        /// <summary>すべての動きが落ち着く時刻（静止画はこの時刻のコマ）。</summary>
        public const float SettledSeconds = 3.2f;

        private readonly MotionScene _scene;
        private readonly int _w;
        private readonly int _h;
        private readonly float _u;
        private readonly bool _landscape;
        private readonly SKTypeface _typeface;
        private readonly SKBitmap _backA;
        private readonly SKBitmap? _backB;
        private readonly SKBitmap? _photo;
        private readonly SKBitmap? _device;
        private readonly SKColor _accent;
        private readonly SKColor _highlight;
        private readonly List<(float X, float Y, float R, float Speed, float K)> _particles;

        public Stage(MotionScene scene, int w, int h, SKTypeface typeface)
        {
            _scene = scene;
            _w = w;
            _h = h;
            _u = Math.Min(w, h) / 1080f;
            _landscape = w > h;
            _typeface = typeface;
            var (bw, bh) = ((int)(w * 1.12), (int)(h * 1.12));
            if (scene.Background is { } background)
            {
                _backA = SkiaImageProcessor.Cover(background, bw, bh);
            }
            else
            {
                _backA = SkiaImageProcessor.Backdrop(bw, bh, scene.Backdrop);
                _backB = SkiaImageProcessor.Backdrop(bw, bh, scene.Backdrop with { Time = scene.Backdrop.Time + 6, Seed = scene.Backdrop.Seed + 1 });
            }
            if (scene.Layout == MotionLayout.Photo && scene.Image is { } photo) _photo = SkiaImageProcessor.Cover(photo, (int)(w * 1.1), (int)(h * 1.1));
            if (scene.Layout == MotionLayout.Device && scene.Image is { } screen)
            {
                using var shot = SkiaImageProcessor.Decode(screen);
                var (dw, dh) = _landscape ? ((int)(w * 0.6), (int)(h * 0.92)) : (w, (int)(h * 0.66));
                _device = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, SKAlphaType.Premul));
                _device.Erase(SKColors.Transparent);
                SkiaImageProcessor.DrawDevice(_device, shot, 0);
            }
            var (_, accent, highlight) = SkiaImageProcessor.Palette(scene.Backdrop.Colors);
            _accent = scene.Alert ? AlertColor : Vivid(accent);
            _highlight = Vivid(highlight);
            var random = new Random(scene.Backdrop.Seed);
            _particles = [.. Enumerable.Range(0, 42).Select(_ => (random.NextSingle() * w, random.NextSingle() * h, (3 + random.NextSingle() * 16) * _u,
                (10 + random.NextSingle() * 40) * _u, random.NextSingle()))];

            static SKColor Vivid((float R, float G, float B) c)
            {
                var color = new SKColor((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255));
                color.ToHsv(out var hue, out var sat, out _);
                return SKColor.FromHsv(hue, Math.Max(sat, 60), 100);
            }
        }

        public void Dispose()
        {
            _backA.Dispose();
            _backB?.Dispose();
            _photo?.Dispose();
            _device?.Dispose();
        }

        public void Draw(SKCanvas canvas, float t, float duration, bool still = false)
        {
            canvas.Clear(SKColors.Black);
            DrawBackground(canvas, t, duration);
            switch (_scene.Layout)
            {
                case MotionLayout.Cards: Cards(canvas, t); break;
                case MotionLayout.Orb: Orb(canvas, t); break;
                case MotionLayout.Checklist: Checklist(canvas, t); break;
                case MotionLayout.Device: Device(canvas, t, duration); break;
                case MotionLayout.Photo: Photo(canvas, t); break;
                case MotionLayout.CallToAction: CallToAction(canvas, t); break;
                default: Statement(canvas, t); break;
            }
            if (still) return;
            // シーンの切り替わり：始めは暗いところから、終わりは少し暗く（つなぎ目をやわらげる）
            var fade = Math.Max(1 - t / 0.25f, (t - (duration - 0.18f)) / 0.18f);
            if (fade > 0)
            {
                using var dark = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(Math.Clamp(fade, 0, 1) * 255)) };
                canvas.DrawRect(0, 0, _w, _h, dark);
            }
        }

        // ---- 背景 ----

        private void DrawBackground(SKCanvas canvas, float t, float duration)
        {
            var progress = Math.Clamp(t / Math.Max(1, duration), 0, 1);
            if (_photo is not null)
            {
                // 写真：ゆっくりズームイン（Ken Burns）
                DrawCentered(canvas, _photo, 1.0f + 0.06f * progress, 0, 0, 255);
            }
            else
            {
                DrawCentered(canvas, _backA, 1.0f + 0.05f * progress, MathF.Sin(t * 0.25f) * 14 * _u, MathF.Cos(t * 0.2f) * 10 * _u, 255);
                if (_backB is not null)
                {
                    var alpha = (byte)(110 + 110 * MathF.Sin(t * 0.7f));
                    DrawCentered(canvas, _backB, 1.04f - 0.03f * progress, -MathF.Sin(t * 0.3f) * 18 * _u, 0, alpha);
                }
            }
            // 光の粒（ぼけた円がゆっくり上へ）
            using var paint = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * _u) };
            foreach (var (x, y, r, speed, k) in _particles)
            {
                var py = ((y - t * speed) % _h + _h) % _h;
                var px = x + MathF.Sin(t * 0.6f + k * 9) * 12 * _u;
                var twinkle = 0.5f + 0.5f * MathF.Sin(t * 1.8f + k * 20);
                paint.Color = (k > 0.5f ? _highlight : _accent).WithAlpha((byte)(30 + 70 * twinkle * k));
                canvas.DrawCircle(px, py, r, paint);
            }
        }

        private void DrawCentered(SKCanvas canvas, SKBitmap bitmap, float zoom, float dx, float dy, byte alpha)
        {
            var scale = Math.Max((float)_w / bitmap.Width, (float)_h / bitmap.Height) * zoom;
            var dw = bitmap.Width * scale;
            var dh = bitmap.Height * scale;
            var rect = SKRect.Create((_w - dw) / 2 + dx, (_h - dh) / 2 + dy, dw, dh);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        }

        // ---- 文字 ----

        private static float Ease(float x) => 1 - MathF.Pow(1 - Math.Clamp(x, 0, 1), 3);

        private static float Back(float x)
        {
            x = Math.Clamp(x, 0, 1);
            const float c1 = 1.70158f, c3 = c1 + 1;
            return 1 + c3 * MathF.Pow(x - 1, 3) + c1 * MathF.Pow(x - 1, 2);
        }

        /// <summary>幅と行数に収まる大きさの文字（大きい方から探す）。</summary>
        private (SKFont Font, List<string> Lines) Fit(string text, float maxWidth, float start, float min, int maxLines)
        {
            for (var size = start; size > min; size *= 0.94f)
            {
                var font = SkiaImageProcessor.CreateFont(_typeface, size);
                var lines = SkiaImageProcessor.Wrap(text, font, maxWidth);
                if (lines.Count <= maxLines) return (font, lines);
                font.Dispose();
            }
            var smallest = SkiaImageProcessor.CreateFont(_typeface, min);
            return (smallest, SkiaImageProcessor.Wrap(text, smallest, maxWidth));
        }

        /// <summary>
        /// 見出しを描く（行ごとに下から浮かび上がり、少し大きくなって落ち着く）。光（グロー）と影で、背景の上でも読めるようにする。
        /// <paramref name="top"/> は1行目の上端。描いた下端を返す。
        /// </summary>
        private float Title(SKCanvas canvas, string text, float centerX, float top, float maxWidth, float size, int maxLines, float t,
            SKColor? glow = null, SKColor? color = null, float stagger = 0.12f, SKTextAlign align = SKTextAlign.Center)
        {
            if (string.IsNullOrWhiteSpace(text)) return top;
            var (font, lines) = Fit(text, maxWidth, size, size * 0.45f, maxLines);
            using (font)
            {
                var lineHeight = SkiaImageProcessor.LineHeight(font) * 0.92f;
                using var glowPaint = new SKPaint
                {
                    IsAntialias = true, Color = (glow ?? _accent).WithAlpha(150),
                    ImageFilter = SKImageFilter.CreateBlur(font.Size * 0.22f, font.Size * 0.22f),
                };
                using var paint = new SKPaint
                {
                    IsAntialias = true, Color = color ?? SKColors.White,
                    ImageFilter = SKImageFilter.CreateDropShadow(0, font.Size * 0.05f, font.Size * 0.08f, font.Size * 0.08f, SKColors.Black.WithAlpha(160)),
                };
                for (var i = 0; i < lines.Count; i++)
                {
                    var p = Ease((t - i * stagger) / 0.55f);
                    if (p <= 0) continue;
                    var baseline = top + i * lineHeight - font.Metrics.Ascent;
                    canvas.Save();
                    canvas.Translate(centerX, baseline + (1 - p) * font.Size * 0.6f);
                    var scale = 0.92f + 0.08f * Back((t - i * stagger) / 0.55f);
                    canvas.Scale(scale);
                    glowPaint.Color = glowPaint.Color.WithAlpha((byte)(150 * p));
                    paint.Color = paint.Color.WithAlpha((byte)(255 * p));
                    canvas.DrawText(lines[i], 0, 0, align, font, glowPaint);
                    canvas.DrawText(lines[i], 0, 0, align, font, paint);
                    canvas.Restore();
                }
                return top + lines.Count * lineHeight;
            }
        }

        /// <summary>小さな言葉（字間を少しあけ、ふわっと出す）。</summary>
        private void Small(SKCanvas canvas, string? text, float centerX, float baseline, float size, SKColor color, float t)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var (font, lines) = Fit(text, _w * 0.86f, size, size * 0.6f, 1);
            using (font)
            {
                var p = Ease(t / 0.5f);
                using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)(255 * p)) };
                canvas.DrawText(lines[0], centerX, baseline + (1 - p) * 20 * _u, SKTextAlign.Center, font, paint);
            }
        }

        private float TitleWidth => _landscape ? _w * 0.72f : _w * 0.86f;

        /// <summary>見出しの高さ（描かずに測る）。</summary>
        private float TitleHeight(string text, float size, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var (font, lines) = Fit(text, TitleWidth, size, size * 0.45f, maxLines);
            using (font) return lines.Count * SkiaImageProcessor.LineHeight(font) * 0.92f;
        }

        /// <summary>見出しと中身のまとまりを、使える範囲（縦型は下の 20% をあける）の上下中央に置いたときの上端。</summary>
        private float GroupTop(float height)
        {
            var (top, bottom) = _landscape ? (_h * 0.06f, _h * 0.96f) : (_h * 0.07f, _h * 0.8f);
            return Math.Max(top, top + (bottom - top - height) / 2);
        }

        // ---- レイアウト ----

        private void Statement(SKCanvas canvas, float t)
        {
            var cx = _w / 2f;
            var top = _h * (_landscape ? 0.3f : 0.36f);
            Small(canvas, _scene.Kicker, cx, top - 40 * _u, 44 * _u, _highlight, t);
            var bottom = Title(canvas, _scene.Title, cx, top, TitleWidth, 112 * _u, 3, t - 0.15f);
            // 見出しの下の線（左右にのびる）
            var p = Ease((t - 0.6f) / 0.6f);
            if (p > 0)
            {
                using var bar = new SKPaint { IsAntialias = true, Color = _accent, ImageFilter = SKImageFilter.CreateBlur(2 * _u, 2 * _u) };
                var half = _w * 0.14f * p;
                canvas.DrawRoundRect(SKRect.Create(cx - half, bottom + 26 * _u, half * 2, 10 * _u), 5 * _u, 5 * _u, bar);
            }
            bottom += 50 * _u;
            if (_scene.Items.FirstOrDefault() is { } sub) bottom = Title(canvas, sub, cx, bottom + 20 * _u, TitleWidth, 52 * _u, 2, t - 0.9f, color: new SKColor(0xE6, 0xEC, 0xF5));
            bottom = Button(canvas, _scene.Button, cx, bottom + 60 * _u, t - 1.1f);
            Small(canvas, _scene.Footer, cx, bottom + 80 * _u, 36 * _u, new SKColor(0xC8, 0xD2, 0xE4), t - 1.4f);
        }

        private void Cards(SKCanvas canvas, float t)
        {
            var cx = _w / 2f;
            var items = _scene.Items.Take(4).ToList();
            var maxBlock = (_landscape ? _h * 0.62f : _h * 0.5f);
            var spacing = items.Count == 0 ? 0 : Math.Min(215 * _u, maxBlock / items.Count);
            var titleHeight = TitleHeight(_scene.Title, 96 * _u, 2);
            var bottom = Title(canvas, _scene.Title, cx, GroupTop(titleHeight + 70 * _u + spacing * items.Count), TitleWidth, 96 * _u, 2, t, glow: _accent);
            if (items.Count == 0) return;
            var areaTop = bottom + 70 * _u;
            var cardH = spacing * 0.82f;
            var cardW = Math.Min(_w * 0.84f, 1100 * _u);
            for (var i = 0; i < items.Count; i++)
            {
                var start = 0.45f + i * 0.32f;
                var p = Ease((t - start) / 0.5f);
                if (p <= 0) continue;
                var y = areaTop + i * spacing;
                var x = (_w - cardW) / 2 + (1 - p) * _w * 0.7f;
                var tilt = (i % 2 == 0 ? -1.6f : 1.4f) * (1 - p) * 4 + (i % 2 == 0 ? -0.8f : 0.8f);
                canvas.Save();
                canvas.RotateDegrees(tilt, x + cardW / 2, y + cardH / 2);
                var rect = SKRect.Create(x, y, cardW, cardH);
                using (var glow = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha((byte)(70 * p)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18 * _u) })
                {
                    canvas.DrawRoundRect(rect, 24 * _u, 24 * _u, glow);
                }
                using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0x0C, 0x12, 0x26, (byte)(215 * p)) })
                {
                    canvas.DrawRoundRect(rect, 24 * _u, 24 * _u, fill);
                }
                using (var border = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 3 * _u, Color = _accent.WithAlpha((byte)(230 * p)) })
                {
                    canvas.DrawRoundRect(rect, 24 * _u, 24 * _u, border);
                }
                // 「!」のしるし
                var r = cardH * 0.26f;
                var icon = new SKPoint(x + cardH * 0.5f, y + cardH / 2);
                using (var badge = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha((byte)(255 * p)) })
                {
                    canvas.DrawCircle(icon, r, badge);
                }
                using (var mark = SkiaImageProcessor.CreateFont(_typeface, r * 1.4f))
                using (var markPaint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * p)) })
                {
                    canvas.DrawText("!", icon.X, icon.Y + r * 0.5f, SKTextAlign.Center, mark, markPaint);
                }
                var (font, lines) = Fit(items[i], cardW - cardH * 1.1f, cardH * 0.36f, cardH * 0.22f, 1);
                using (font)
                using (var text = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * p)) })
                {
                    canvas.DrawText(lines[0], x + cardH * 0.95f, y + cardH / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2, SKTextAlign.Left, font, text);
                }
                canvas.Restore();
            }
        }

        private void Orb(SKCanvas canvas, float t)
        {
            var cx = _w / 2f;
            var full = _landscape ? _h * 0.2f : _w * 0.24f;
            var chips = Math.Min(2, _scene.Items.Count) * 110 * _u;
            var titleHeight = TitleHeight(_scene.Title, 96 * _u, 2);
            var bottom = Title(canvas, _scene.Title, cx, GroupTop(titleHeight + full * 2.9f + chips), TitleWidth, 96 * _u, 2, t);
            var radius = full * Back((t - 0.25f) / 0.8f);
            var cy = bottom + full * 1.45f;
            if (radius <= 1) return;
            var pulse = 1 + 0.04f * MathF.Sin(t * 5);
            using (var halo = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha(110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * 0.6f) })
            {
                canvas.DrawCircle(cx, cy, radius * 1.3f * pulse, halo);
            }
            // 広がる輪
            using (var ring = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 4 * _u })
            {
                for (var k = 0; k < 3; k++)
                {
                    var phase = (t * 0.6f + k / 3f) % 1;
                    ring.Color = _highlight.WithAlpha((byte)(180 * (1 - phase)));
                    canvas.DrawCircle(cx, cy, radius * (1 + phase * 0.9f), ring);
                }
            }
            using (var core = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy - radius * 0.3f), radius * 1.2f,
                           [_highlight.WithAlpha(235), _accent.WithAlpha(235), new SKColor(0x08, 0x10, 0x28, 235)], [0, 0.55f, 1], SKShaderTileMode.Clamp),
                   })
            {
                canvas.DrawCircle(cx, cy, radius * pulse, core);
            }
            using (var edge = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 6 * _u, Color = SKColors.White.WithAlpha(200) })
            {
                canvas.DrawCircle(cx, cy, radius * pulse, edge);
            }
            // 円の中：商品・サービス名（なければチェックのしるし）
            if (!string.IsNullOrWhiteSpace(_scene.Kicker))
            {
                // 商品名はできるだけ1行で（英字の名前を途中で折らない）。入らなければ2行
                var (font, lines) = Fit(_scene.Kicker, radius * 1.6f, radius * 0.42f, radius * 0.2f, 1);
                if (lines.Count > 1)
                {
                    font.Dispose();
                    (font, lines) = Fit(_scene.Kicker, radius * 1.5f, radius * 0.42f, radius * 0.16f, 2);
                }
                using (font)
                using (var text = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * Ease((t - 0.7f) / 0.4f))) })
                {
                    var lh = SkiaImageProcessor.LineHeight(font) * 0.9f;
                    var y = cy - lh * lines.Count / 2 - font.Metrics.Ascent;
                    foreach (var line in lines)
                    {
                        canvas.DrawText(line, cx, y, SKTextAlign.Center, font, text);
                        y += lh;
                    }
                }
            }
            else
            {
                Check(canvas, cx, cy, radius * 0.5f, Ease((t - 0.7f) / 0.5f), SKColors.White);
            }
            var items = _scene.Items.Take(2).ToList();
            var chipTop = cy + full * 1.45f;
            for (var i = 0; i < items.Count && chipTop < _h * 0.9f; i++)
            {
                chipTop = Chip(canvas, items[i], cx, chipTop, t - 1.0f - i * 0.25f) + 18 * _u;
            }
        }

        /// <summary>丸い枠の中の言葉（ふわっと出す）。下端を返す。</summary>
        private float Chip(SKCanvas canvas, string text, float cx, float top, float t)
        {
            var p = Ease(t / 0.45f);
            var (font, lines) = Fit(text, _w * 0.8f, 46 * _u, 30 * _u, 1);
            using (font)
            {
                var width = font.MeasureText(lines[0]) + 80 * _u;
                var height = font.Size * 1.9f;
                if (p <= 0) return top + height;
                var rect = SKRect.Create(cx - width / 2, top + (1 - p) * 20 * _u, width, height);
                using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(36 * p)) };
                using var border = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 2 * _u, Color = _highlight.WithAlpha((byte)(200 * p)) };
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * p)) };
                canvas.DrawRoundRect(rect, height / 2, height / 2, fill);
                canvas.DrawRoundRect(rect, height / 2, height / 2, border);
                canvas.DrawText(lines[0], cx, rect.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2, SKTextAlign.Center, font, paint);
                return top + height;
            }
        }

        /// <summary>チェックのしるし（線が描かれていく）。</summary>
        private void Check(SKCanvas canvas, float cx, float cy, float size, float p, SKColor color)
        {
            if (p <= 0) return;
            // 2本の線分を、長さの割合に合わせて描き足していく
            var a = new SKPoint(cx - size * 0.55f, cy);
            var b = new SKPoint(cx - size * 0.15f, cy + size * 0.42f);
            var c = new SKPoint(cx + size * 0.6f, cy - size * 0.45f);
            var first = SKPoint.Distance(a, b);
            var total = first + SKPoint.Distance(b, c);
            var drawn = total * Math.Clamp(p, 0, 1);
            using var paint = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = size * 0.22f, StrokeCap = SKStrokeCap.Round, Color = color };
            canvas.DrawLine(a, Lerp(a, b, Math.Min(1, drawn / first)), paint);
            if (drawn > first) canvas.DrawLine(b, Lerp(b, c, (drawn - first) / (total - first)), paint);

            static SKPoint Lerp(SKPoint from, SKPoint to, float k) => new(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k);
        }

        private void Checklist(SKCanvas canvas, float t)
        {
            var cx = _w / 2f;
            var items = _scene.Items.Take(4).ToList();
            var maxBlock = (_landscape ? _h * 0.62f : _h * 0.5f);
            var spacing = items.Count == 0 ? 0 : Math.Min(215 * _u, maxBlock / items.Count);
            var titleHeight = TitleHeight(_scene.Title, 96 * _u, 2);
            var bottom = Title(canvas, _scene.Title, cx, GroupTop(titleHeight + 70 * _u + spacing * items.Count), TitleWidth, 96 * _u, 2, t);
            if (items.Count == 0) return;
            var areaTop = bottom + 70 * _u;
            var rowH = spacing * 0.78f;
            var rowW = Math.Min(_w * 0.86f, 1150 * _u);
            var left = (_w - rowW) / 2;
            for (var i = 0; i < items.Count; i++)
            {
                var start = 0.45f + i * 0.4f;
                var p = Ease((t - start) / 0.45f);
                if (p <= 0) continue;
                var y = areaTop + i * spacing;
                var x = left - (1 - p) * 80 * _u;
                var rect = SKRect.Create(x, y, rowW, rowH);
                using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(28 * p)) })
                {
                    canvas.DrawRoundRect(rect, rowH / 2, rowH / 2, fill);
                }
                var r = rowH * 0.32f;
                var icon = new SKPoint(x + rowH * 0.5f, y + rowH / 2);
                using (var badge = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha((byte)(255 * p)) })
                using (var glow = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha((byte)(120 * p)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.6f) })
                {
                    canvas.DrawCircle(icon, r * 1.2f, glow);
                    canvas.DrawCircle(icon, r, badge);
                }
                Check(canvas, icon.X, icon.Y, r * 0.75f, Ease((t - start - 0.2f) / 0.35f), SKColors.White);
                var (font, lines) = Fit(items[i], rowW - rowH * 1.2f, rowH * 0.4f, rowH * 0.24f, 1);
                using (font)
                using (var text = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * p)) })
                {
                    canvas.DrawText(lines[0], x + rowH * 1.0f, y + rowH / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2, SKTextAlign.Left, font, text);
                }
            }
        }

        private void Device(SKCanvas canvas, float t, float duration)
        {
            if (_device is null)
            {
                Statement(canvas, t);
                return;
            }
            var p = Ease((t - 0.1f) / 0.8f);
            var zoom = 1 + 0.035f * Math.Clamp(t / Math.Max(1, duration), 0, 1);
            if (_landscape)
            {
                // 横型：左に見出し、右に端末
                Title(canvas, _scene.Title, _w * 0.22f, _h * 0.3f, _w * 0.36f, 84 * _u, 4, t);
                if (_scene.Items.FirstOrDefault() is { } item) Chip(canvas, item, _w * 0.22f, _h * 0.72f, t - 1.2f);
                DrawLayer(canvas, _device, _w * 0.68f, _h * 0.5f + (1 - p) * _h * 0.5f, zoom, p);
            }
            else
            {
                var bottom = Title(canvas, _scene.Title, _w / 2f, _h * 0.08f, TitleWidth, 88 * _u, 2, t);
                var center = Math.Max(bottom + 30 * _u, _h * 0.2f) + _device.Height / 2f;
                DrawLayer(canvas, _device, _w / 2f, center + (1 - p) * _h * 0.5f, zoom, p);
                if (_scene.Items.FirstOrDefault() is { } item) Chip(canvas, item, _w / 2f, Math.Min(_h * 0.78f, center + _device.Height / 2f), t - 1.2f);
            }
        }

        private static void DrawLayer(SKCanvas canvas, SKBitmap layer, float cx, float cy, float zoom, float alpha)
        {
            var w = layer.Width * zoom;
            var h = layer.Height * zoom;
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(alpha, 0, 1))) };
            using var image = SKImage.FromBitmap(layer);
            canvas.DrawImage(image, SKRect.Create(cx - w / 2, cy - h / 2, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        }

        private void Photo(SKCanvas canvas, float t)
        {
            // 下を暗くするグラデーション（写真の上でも見出しが読める）
            using (var shade = new SKPaint
                   {
                       Shader = SKShader.CreateLinearGradient(new SKPoint(0, _h * 0.45f), new SKPoint(0, _h),
                           [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(200)], SKShaderTileMode.Clamp),
                   })
            {
                canvas.DrawRect(0, 0, _w, _h, shade);
            }
            var top = _landscape ? _h * 0.66f : _h * 0.6f;
            var bottom = Title(canvas, _scene.Title, _w / 2f, top, TitleWidth, 96 * _u, 2, t - 0.2f);
            if (_scene.Items.FirstOrDefault() is { } item && bottom + 120 * _u < _h * (_landscape ? 0.97f : 0.8f))
            {
                Chip(canvas, item, _w / 2f, bottom + 24 * _u, t - 0.9f);
            }
        }

        /// <summary>ボタン（はずむように現れ、ゆっくり脈打ち、光が横切る）。下端を返す。</summary>
        private float Button(SKCanvas canvas, string? text, float cx, float top, float t)
        {
            var bottom = top;
            if (string.IsNullOrWhiteSpace(text)) return bottom;
            var p = Back(t / 0.6f);
            var (font, lines) = Fit(text, _w * 0.62f, 60 * _u, 36 * _u, 1);
            using (font)
            {
                var pulse = 1 + 0.035f * MathF.Sin(t * 4.5f) * Math.Clamp(t - 0.6f, 0, 1);
                var bw = (font.MeasureText(lines[0]) + 140 * _u) * pulse * Math.Max(0, p);
                var bh = font.Size * 2.3f * pulse * Math.Max(0, p);
                var by = top;
                if (bw > 1 && bh > 1)
                {
                    var rect = SKRect.Create(cx - bw / 2, by, bw, bh);
                    using (var glow = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha(150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 30 * _u) })
                    {
                        canvas.DrawRoundRect(rect, bh / 2, bh / 2, glow);
                    }
                    using (var fill = new SKPaint
                           {
                               IsAntialias = true,
                               Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                                   [_accent, _highlight], SKShaderTileMode.Clamp),
                           })
                    {
                        canvas.DrawRoundRect(rect, bh / 2, bh / 2, fill);
                    }
                    // ボタンを横切る光
                    var sweep = (t * 0.6f) % 1.6f;
                    if (p >= 1 && sweep < 1)
                    {
                        canvas.Save();
                        using var clip = new SKRoundRect(rect, bh / 2, bh / 2);
                        canvas.ClipRoundRect(clip, antialias: true);
                        using var shine = new SKPaint
                        {
                            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left + rect.Width * (sweep * 1.4f - 0.3f), 0),
                                new SKPoint(rect.Left + rect.Width * (sweep * 1.4f - 0.1f), 0),
                                [SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(90), SKColors.White.WithAlpha(0)], [0, 0.5f, 1], SKShaderTileMode.Clamp),
                        };
                        canvas.DrawRect(rect, shine);
                        canvas.Restore();
                    }
                    if (p > 0.6f)
                    {
                        using var label = new SKPaint { IsAntialias = true, Color = SKColors.White };
                        using var scaled = SkiaImageProcessor.CreateFont(_typeface, font.Size * pulse);
                        canvas.DrawText(lines[0], cx, rect.MidY - (scaled.Metrics.Ascent + scaled.Metrics.Descent) / 2, SKTextAlign.Center, scaled, label);
                    }
                    bottom = by + bh;
                }
            }
            return bottom;
        }

        private void CallToAction(SKCanvas canvas, float t)
        {
            var cx = _w / 2f;
            var top = _h * (_landscape ? 0.18f : 0.26f);
            var bottom = Title(canvas, _scene.Kicker ?? "", cx, top, TitleWidth, 128 * _u, 2, t, glow: _highlight);
            bottom = Title(canvas, _scene.Title, cx, bottom + 30 * _u, TitleWidth, 60 * _u, 2, t - 0.35f, color: new SKColor(0xE6, 0xEC, 0xF5));
            bottom = Button(canvas, _scene.Button, cx, bottom + 70 * _u, t - 0.8f);
            Small(canvas, _scene.Footer, cx, bottom + 90 * _u, 40 * _u, new SKColor(0xC8, 0xD2, 0xE4), t - 1.3f);
        }
    }
}
