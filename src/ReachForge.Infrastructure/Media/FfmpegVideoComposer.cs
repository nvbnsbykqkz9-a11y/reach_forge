using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Common;

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

    /// <summary>ライセンス済みの BGM を置くフォルダ（tracks.json と曲のファイル）。未設定なら組み込みの曲だけ。</summary>
    public string? BgmLibraryPath { get; set; }
}

/// <summary>
/// FFmpeg による動画の合成（F-05 ③ 処理 4〜5）。各シーンの静止画にゆっくりズーム（Ken Burns）をかけて連結し
/// （生成 AI のクリップのシーンはクリップにテロップを重ね）、
/// シーンごとのナレーションを無音で埋めて同じ長さにそろえて連結する。
/// 出力は 1080×1920・30fps・H.264（yuv420p）／AAC・moov 先頭（faststart）。
/// 引数は配列で渡し（シェルを通さない）、作業フォルダは処理後に削除する。
/// </summary>
public sealed class FfmpegVideoComposer(IOptions<VideoOptions> options, BgmLibrary bgm, ILogger<FfmpegVideoComposer> log) : IVideoComposer
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int Fps = 30;

    public const string NotFoundMessage =
        "動画の書き出しに必要な ffmpeg が見つかりません。ffmpeg（H.264／libx264 に対応したもの）をインストールして PATH を通すか、" +
        "設定ファイル（appsettings.user.json）の Video:FfmpegPath に ffmpeg.exe の場所を指定して、アプリを再起動してください。";

    public const string NoX264Message =
        "この ffmpeg は H.264（libx264）での書き出しに対応していません。libx264 を含む ffmpeg（「full」や「essentials」などの GPL 版）を使ってください。";

    private (DateTimeOffset At, string? Problem)? _check;

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        // 何度も ffmpeg を起動しないよう、結果を1分おぼえておく（ffmpeg を入れた後はすぐに確かめ直せるよう短め）
        if (_check is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Problem;
        string? problem;
        try
        {
            var (_, output) = await StartAsync(["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct);
            problem = output.Contains("libx264", StringComparison.Ordinal) ? null : NoX264Message;
        }
        catch (DomainException ex)
        {
            problem = ex.Message;
        }
        _check = (DateTimeOffset.UtcNow, problem);
        return problem;
    }

    public async Task<ComposedVideo> ComposeAsync(IReadOnlyList<VideoSceneInput> scenes, CancellationToken ct, VideoAudioOptions? audio = null,
        (int Width, int Height)? size = null)
    {
        var (width, height) = size ?? (Width, Height);
        if (scenes.Count == 0) throw new ArgumentException("シーンがありません。", nameof(scenes));
        var dir = Directory.CreateTempSubdirectory("rf-video-");
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].Clip is { } clip)
                {
                    // 生成 AI のクリップ：短ければ繰り返してシーンの長さにそろえる
                    var path = Path.Combine(dir.FullName, $"scene{i}.mp4");
                    await File.WriteAllBytesAsync(path, clip, ct);
                    args.AddRange(["-stream_loop", "-1", "-t", Sec(scenes[i].Seconds), "-i", path]);
                    continue;
                }
                var image = Path.Combine(dir.FullName, $"scene{i}.jpg");
                await File.WriteAllBytesAsync(image, scenes[i].Image, ct);
                args.AddRange(["-loop", "1", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-t", Sec(scenes[i].Seconds), "-i", image]);
            }
            // クリップに重ねるテロップ（透明な PNG）
            var overlayInputs = new Dictionary<int, int>();
            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].Clip is null || scenes[i].OverlayPng is not { } png) continue;
                var path = Path.Combine(dir.FullName, $"caption{i}.png");
                await File.WriteAllBytesAsync(path, png, ct);
                args.AddRange(["-i", path]);
                overlayInputs[i] = scenes.Count + overlayInputs.Count;
            }
            var firstAudio = scenes.Count + overlayInputs.Count;
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
                audioInputs.Add(firstAudio + audioInputs.Count(x => x >= 0));
            }

            var filters = new List<string>();
            for (var i = 0; i < scenes.Count; i++)
            {
                var frames = (int)Math.Round(scenes[i].Seconds * Fps);
                if (scenes[i].Clip is not null)
                {
                    // クリップは書き出す大きさを覆うように拡大して中央を切り出し（引き伸ばさない）、長さをそろえる
                    var fit = $"[{i}:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},fps={Fps},setsar=1," +
                              $"trim=duration={Sec(scenes[i].Seconds)},setpts=PTS-STARTPTS";
                    filters.Add(overlayInputs.TryGetValue(i, out var caption)
                        ? $"{fit}[c{i}];[c{i}][{caption}:v]overlay=0:0:format=auto,format=yuv420p[v{i}]"
                        : $"{fit},format=yuv420p[v{i}]");
                }
                else
                {
                    // わずかにズームインして静止画に動きを出す（1.0 → 1.06）
                    // 画像は比率を保ったまま枠を覆う大きさにしてから（引き伸ばさない）ズームする
                    filters.Add($"[{i}:v]scale={width * 2}:{height * 2}:force_original_aspect_ratio=increase,crop={width * 2}:{height * 2},zoompan=z='min(1+0.06*on/{Math.Max(1, frames)},1.06)':" +
                                $"x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={width}x{height}:fps={Fps},setsar=1,format=yuv420p[v{i}]");
                }
                var a = audioInputs[i];
                filters.Add(a >= 0
                    ? $"[{a}:a]aformat=sample_rates=44100:channel_layouts=stereo,apad,atrim=0:{Sec(scenes[i].Seconds)},asetpts=N/SR/TB[a{i}]"
                    : $"anullsrc=r=44100:cl=stereo,atrim=0:{Sec(scenes[i].Seconds)},asetpts=N/SR/TB[a{i}]");
            }
            var concatInputs = string.Concat(Enumerable.Range(0, scenes.Count).Select(i => $"[v{i}][a{i}]"));
            filters.Add($"{concatInputs}concat=n={scenes.Count}:v=1:a=1[v][voice]");
            var total = scenes.Sum(s => s.Seconds);
            var nextInput = firstAudio + audioInputs.Count(x => x >= 0);
            AddBgm(args, filters, "voice", "a", total, nextInput, audio);

            var output = Path.Combine(dir.FullName, "out.mp4");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[v]", "-map", "[a]",
                "-c:v", "libx264", "-preset", options.Value.Preset, "-crf", "23", "-pix_fmt", "yuv420p", "-r", Fps.ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", output]);

            await RunAsync(args, ct);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            var durationMs = (int)Math.Round(scenes.Sum(s => s.Seconds) * 1000);
            return new ComposedVideo(bytes, durationMs, width, height);
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

    public async Task<ComposedVideo> FinishClipAsync(byte[] clip, VideoAudioOptions? audio, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("rf-clip-");
        try
        {
            var input = Path.Combine(dir.FullName, "clip.mp4");
            await File.WriteAllBytesAsync(input, clip, ct);
            var (duration, hasAudio) = await ProbeAsync(input, ct);
            if (duration <= 0) throw new DomainException(ErrorCodes.VideoUnavailable, "AI がつくった動画の長さを読み取れませんでした。もう一度お試しください。");

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input };
            var filters = new List<string>
            {
                // 縦型 1080×1920 に収め（引き伸ばさない）、30fps・yuv420p にそろえる
                $"[0:v]scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={Fps},format=yuv420p[v]",
                hasAudio
                    ? $"[0:a]aformat=sample_rates=44100:channel_layouts=stereo,atrim=0:{Sec(duration)},asetpts=N/SR/TB[base]"
                    : $"anullsrc=r=44100:cl=stereo,atrim=0:{Sec(duration)},asetpts=N/SR/TB[base]",
            };
            AddBgm(args, filters, "base", "a", duration, 1, audio);
            var output = Path.Combine(dir.FullName, "out.mp4");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[v]", "-map", "[a]", "-t", Sec(duration),
                "-c:v", "libx264", "-preset", options.Value.Preset, "-crf", "23", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", output]);
            await RunAsync(args, ct);
            return new ComposedVideo(await File.ReadAllBytesAsync(output, ct), (int)Math.Round(duration * 1000), Width, Height);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public async Task<byte[]> ExtractFrameAsync(byte[] mp4, double seconds, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("rf-frame-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.mp4");
            var output = Path.Combine(dir.FullName, "frame.jpg");
            await File.WriteAllBytesAsync(input, mp4, ct);
            await RunAsync(["-hide_banner", "-loglevel", "error", "-y", "-ss", Sec(seconds), "-i", input, "-frames:v", "1", "-q:v", "3", output], ct);
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// BGM を重ねる：ナレーション（<paramref name="main"/>）の音量に合わせて BGM を下げ（サイドチェインのダッキング）、
    /// 最初と最後はフェードする。BGM を使わない場合は <paramref name="main"/> をそのまま出力ラベルにする。
    /// </summary>
    private void AddBgm(List<string> args, List<string> filters, string main, string output, double seconds, int inputIndex,
        VideoAudioOptions? audio)
    {
        var track = audio?.BgmTrackId is { Length: > 0 } id ? bgm.Resolve(id) : null;
        if (track is null)
        {
            filters.Add($"[{main}]anull[{output}]");
            return;
        }
        var volume = Math.Clamp(audio!.BgmVolume, 0.05, 0.6).ToString("0.##", CultureInfo.InvariantCulture);
        var fadeOut = Sec(Math.Max(0, seconds - 1.5));
        string source;
        if (track.Value.Expression is { } expression)
        {
            source = $"aevalsrc='{expression}':s=44100:d={Sec(seconds)},aformat=sample_rates=44100:channel_layouts=stereo";
        }
        else
        {
            args.AddRange(["-stream_loop", "-1", "-i", track.Value.File!]);
            source = $"[{inputIndex}:a]aformat=sample_rates=44100:channel_layouts=stereo";
        }
        filters.Add($"[{main}]asplit=2[{main}_mix][{main}_key]");
        filters.Add($"{source},volume={volume},atrim=0:{Sec(seconds)},asetpts=N/SR/TB,afade=t=in:d=1,afade=t=out:st={fadeOut}:d=1.5[bgm]");
        filters.Add($"[bgm][{main}_key]sidechaincompress=threshold=0.02:ratio=10:attack=15:release=350[ducked]");
        filters.Add($"[{main}_mix][ducked]amix=inputs=2:duration=first:normalize=0[{output}]");
    }

    /// <summary>長さ（秒）と音声の有無を調べる（ffmpeg -i の出力を読む）。</summary>
    private async Task<(double Seconds, bool HasAudio)> ProbeAsync(string path, CancellationToken ct)
    {
        var (_, info) = await StartAsync(["-hide_banner", "-i", path], TimeSpan.FromSeconds(60), ct);
        var match = System.Text.RegularExpressions.Regex.Match(info, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        var seconds = match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
              + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        return (seconds, info.Contains("Audio:", StringComparison.Ordinal));
    }

    private void TryDelete(DirectoryInfo dir)
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

    private async Task RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (exitCode, output) = await StartAsync(args, TimeSpan.FromSeconds(options.Value.TimeoutSeconds), ct);
        if (exitCode != 0)
        {
            log.LogError("ffmpeg failed ({Code}): {Error}", exitCode, output.Length > 2000 ? output[^2000..] : output);
            throw new DomainException(ErrorCodes.VideoUnavailable, Describe(output));
        }
    }

    /// <summary>ffmpeg を実行し、終了コードと出力（標準出力＋標準エラー）を返す。見つからない・時間切れは理由を添えて知らせる。</summary>
    private async Task<(int ExitCode, string Output)> StartAsync(IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
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
            log.LogError(ex, "ffmpeg was not found at {Path}", options.Value.FfmpegPath);
            throw new DomainException(ErrorCodes.VideoUnavailable, NotFoundMessage);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new DomainException(ErrorCodes.VideoUnavailable, "動画の書き出しが時間内に終わりませんでした。動画を短くするか、時間をおいてお試しください。");
        }
    }

    /// <summary>ffmpeg のエラーを、利用者が対処できる言葉にする。</summary>
    internal static string Describe(string output)
    {
        if (output.Contains("Unknown encoder 'libx264'", StringComparison.Ordinal)
            || output.Contains("Encoder not found", StringComparison.Ordinal) && output.Contains("libx264", StringComparison.Ordinal))
        {
            return NoX264Message;
        }
        if (output.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
        {
            return "ディスクの空き容量が足りないため、動画を書き出せませんでした。不要なファイルを削除してからお試しください。";
        }
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return $"動画を書き出せませんでした（ffmpeg：{(last.Length > 160 ? last[..160] : last)}）。もう一度お試しください。";
    }

    private static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
