using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Media;

public sealed class VideoOptions
{
    public const string SectionName = "Video";

    /// <summary>ffmpeg の実行ファイル（PATH 上の名前またはフルパス）。</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>x264 のプリセット（速さと画質のバランス）。</summary>
    public string Preset { get; set; } = "veryfast";

    /// <summary>1本あたりの上限時間（F-05 例外：15分で失敗扱い）。</summary>
    public int TimeoutSeconds { get; set; } = 900;
}

/// <summary>
/// FFmpeg による動画の合成（F-05 ③ 処理 4〜5）。各シーンの静止画にゆっくりズーム（Ken Burns）をかけて連結し、
/// シーンごとのナレーションを無音で埋めて同じ長さにそろえて連結する。
/// 出力は 1080×1920・30fps・H.264（yuv420p）／AAC・moov 先頭（faststart）。
/// 引数は配列で渡し（シェルを通さない）、作業フォルダは処理後に削除する。
/// </summary>
public sealed class FfmpegVideoComposer(IOptions<VideoOptions> options, ILogger<FfmpegVideoComposer> log) : IVideoComposer
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int Fps = 30;

    public async Task<ComposedVideo> ComposeAsync(IReadOnlyList<VideoSceneInput> scenes, CancellationToken ct)
    {
        if (scenes.Count == 0) throw new ArgumentException("シーンがありません。", nameof(scenes));
        var dir = Directory.CreateTempSubdirectory("rf-video-");
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            for (var i = 0; i < scenes.Count; i++)
            {
                var image = Path.Combine(dir.FullName, $"scene{i}.jpg");
                await File.WriteAllBytesAsync(image, scenes[i].Image, ct);
                args.AddRange(["-loop", "1", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-t", Sec(scenes[i].Seconds), "-i", image]);
            }
            var audioInputs = new List<int>();
            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].NarrationWav is not { } wav)
                {
                    audioInputs.Add(-1);
                    continue;
                }
                var path = Path.Combine(dir.FullName, $"voice{i}.wav");
                await File.WriteAllBytesAsync(path, wav, ct);
                args.AddRange(["-i", path]);
                audioInputs.Add(scenes.Count + audioInputs.Count(x => x >= 0));
            }

            var filters = new List<string>();
            for (var i = 0; i < scenes.Count; i++)
            {
                var frames = (int)Math.Round(scenes[i].Seconds * Fps);
                // わずかにズームインして静止画に動きを出す（1.0 → 1.06）
                filters.Add($"[{i}:v]scale={Width * 2}:{Height * 2},zoompan=z='min(1+0.06*on/{Math.Max(1, frames)},1.06)':" +
                            $"x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={Width}x{Height}:fps={Fps},setsar=1,format=yuv420p[v{i}]");
                var a = audioInputs[i];
                filters.Add(a >= 0
                    ? $"[{a}:a]aformat=sample_rates=44100:channel_layouts=stereo,apad,atrim=0:{Sec(scenes[i].Seconds)},asetpts=N/SR/TB[a{i}]"
                    : $"anullsrc=r=44100:cl=stereo,atrim=0:{Sec(scenes[i].Seconds)},asetpts=N/SR/TB[a{i}]");
            }
            var concatInputs = string.Concat(Enumerable.Range(0, scenes.Count).Select(i => $"[v{i}][a{i}]"));
            filters.Add($"{concatInputs}concat=n={scenes.Count}:v=1:a=1[v][a]");

            var output = Path.Combine(dir.FullName, "out.mp4");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[v]", "-map", "[a]",
                "-c:v", "libx264", "-preset", options.Value.Preset, "-crf", "23", "-pix_fmt", "yuv420p", "-r", Fps.ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", output]);

            await RunAsync(args, ct);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            var durationMs = (int)Math.Round(scenes.Sum(s => s.Seconds) * 1000);
            return new ComposedVideo(bytes, durationMs, Width, Height);
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Failed to delete temp video folder {Path}", dir.FullName);
            }
        }
    }

    private async Task RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("ffmpeg が見つかりません。Video:FfmpegPath を設定してください。", ex);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("動画の書き出しが時間内に終わりませんでした。");
        }
        if (process.ExitCode != 0)
        {
            var error = await stderr;
            log.LogError("ffmpeg failed ({Code}): {Error}", process.ExitCode, error.Length > 2000 ? error[..2000] : error);
            throw new InvalidOperationException("動画を書き出せませんでした。");
        }
    }

    private static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
