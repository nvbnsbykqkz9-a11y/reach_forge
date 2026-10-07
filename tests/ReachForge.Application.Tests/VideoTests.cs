using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Media;

namespace ReachForge.Application.Tests;

public class VideoTests
{
    private static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void Srt_follows_scene_timeline()
    {
        var srt = VideoService.BuildSrt([(new VideoScene("A", "ひとつめ", 2.5), 2.5), (new VideoScene("B", "", 3), 3)]);
        Assert.Equal("1\n00:00:00,000 --> 00:00:02,500\nひとつめ\n\n2\n00:00:02,500 --> 00:00:05,500\nB\n\n", srt);
    }

    [Fact]
    public async Task Template_video_job_creates_vertical_mp4_with_thumbnail_and_subtitles()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync();
        Guid jobId;
        await using (var scope = f.Scope())
        {
            var p = new ImageSharpProcessor();
            var media = f.Get<MediaService>(scope);
            var ids = new List<Guid>();
            foreach (var seed in new[] { 1, 2 })
            {
                var bytes = (await p.RenderPlaceholderAsync(1200, 900, seed, ["#B45309", "#FDE68A"], CancellationToken.None)).Bytes;
                ids.Add((await media.UploadAsync($"s{seed}.png", "image/png", new MemoryStream(bytes), bytes.Length, CancellationToken.None)).Id);
            }
            var videos = f.Get<VideoService>(scope);
            await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(new VideoJobRequest("", ids), CancellationToken.None));
            var request = new VideoJobRequest("秋限定さつまいもラテ", ids, Narration: true, TargetSeconds: 10);
            var job = await videos.EnqueueAsync(request, CancellationToken.None);
            Assert.Equal(VideoService.EstimateCredits(request), job.CreditsHeld);
            jobId = job.Id;
        }

        var sw = Stopwatch.StartNew();
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }
        Assert.True(f.Logs.Errors.IsEmpty, string.Join("\n", f.Logs.Errors));

        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var job = await db.AiJobs.SingleAsync(j => j.Id == jobId);
            Assert.True(job.Status == AiJobStatus.Succeeded, job.Error);
            var video = await db.MediaAssets.SingleAsync(m => m.Id == job.ResultAssetIds[0]);
            Assert.Equal((MediaKind.Video, "video/mp4", 1080, 1920), (video.Kind, video.Mime, video.Width, video.Height));
            Assert.InRange(video.DurationMs!.Value, 9000, 31000);
            Assert.True(video.IsAiLabeled);
            Assert.StartsWith("1\n00:00:00,000 --> ", video.SubtitlesSrt);
            var bytes = await f.Get<MediaService>(scope).ReadAsync(video, CancellationToken.None);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
            // faststart：moov が mdat より前にある
            var text = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200_000));
            Assert.True(text.IndexOf("moov", StringComparison.Ordinal) < text.IndexOf("mdat", StringComparison.Ordinal));
            Assert.True(await db.MediaAssets.AnyAsync(m => m.ParentAssetId == video.Id && m.DerivationKey == MediaService.ThumbnailKey));
            Assert.Equal(job.CreditsCharged, VideoService.EstimateCredits(new VideoJobRequest("x", [], true, 10)));
        }
        Console.WriteLine($"video job took {sw.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>音声の平均音量（dB）。無音なら -91 程度。</summary>
    private static double MeanVolume(byte[] mp4)
    {
        var path = Path.GetTempFileName() + ".mp4";
        File.WriteAllBytes(path, mp4);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -i {path} -af volumedetect -f null -")
                { RedirectStandardError = true, RedirectStandardOutput = true })!;
            var log = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(log, @"mean_volume: (-?[\d.]+) dB");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -999;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bgm_is_mixed_under_silence_and_clips_are_finished_to_vertical_hd()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var composer = f.Get<IVideoComposer>(scope);
        var frame = (await new ImageSharpProcessor().EncodeJpegAsync(
            (await new ImageSharpProcessor().RenderPlaceholderAsync(1080, 1920, 3, [], CancellationToken.None)).Bytes, 2_000_000, CancellationToken.None)).Bytes;

        var silent = await composer.ComposeAsync([new VideoSceneInput(frame, 3, null)], CancellationToken.None);
        var withBgm = await composer.ComposeAsync([new VideoSceneInput(frame, 3, null)], CancellationToken.None, new VideoAudioOptions("calm"));
        Assert.True(MeanVolume(silent.Mp4) < -80);
        Assert.InRange(MeanVolume(withBgm.Mp4), -60, -5);

        // 生成 AI のクリップ（横長・音声なし）を 1080×1920 に整え、BGM を重ねる
        var finished = await composer.FinishClipAsync(silent.Mp4, new VideoAudioOptions("bright"), CancellationToken.None);
        Assert.Equal((1080, 1920), (finished.Width, finished.Height));
        Assert.InRange(finished.DurationMs, 2900, 3100);
        Assert.InRange(MeanVolume(finished.Mp4), -60, -5);
        Assert.Equal(0xFF, (await composer.ExtractFrameAsync(finished.Mp4, 1, CancellationToken.None))[0]); // JPEG
        Assert.Contains(f.Get<VideoService>(scope).BgmTracks, t => t.Id == "calm" && t.BuiltIn);
    }

    [Theory]
    [InlineData(VideoMode.TextToVideo)]
    [InlineData(VideoMode.ImageToVideo)]
    public async Task Generative_video_jobs_create_labelled_vertical_videos(VideoMode mode)
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync();
        Guid jobId;
        await using (var scope = f.Scope())
        {
            var ids = new List<Guid>();
            if (mode == VideoMode.ImageToVideo)
            {
                var bytes = (await new ImageSharpProcessor().RenderPlaceholderAsync(1200, 900, 5, [], CancellationToken.None)).Bytes;
                ids.Add((await f.Get<MediaService>(scope).UploadAsync("s.png", "image/png", new MemoryStream(bytes), bytes.Length, CancellationToken.None)).Id);
            }
            var videos = f.Get<VideoService>(scope);
            var job = await videos.EnqueueAsync(new VideoJobRequest("湯気の立つラテ", ids, TargetSeconds: 4, Mode: mode, BgmTrackId: "calm"),
                CancellationToken.None);
            Assert.Equal(CreditTable.Cost(CreditOperation.ShortVideo), job.CreditsHeld);
            jobId = job.Id;
        }
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }
        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var job = await db.AiJobs.SingleAsync(j => j.Id == jobId);
            Assert.True(job.Status == AiJobStatus.Succeeded, job.Error);
            Assert.Equal(CreditTable.Cost(CreditOperation.ShortVideo), job.CreditsCharged);
            var video = await db.MediaAssets.SingleAsync(m => m.Id == job.ResultAssetIds[0]);
            Assert.Equal((1080, 1920), (video.Width, video.Height));
            Assert.InRange(video.DurationMs!.Value, 3500, 4500);
            Assert.Contains(mode == VideoMode.TextToVideo ? "text-to-video" : "image-to-video", video.Provenance);
            Assert.Contains(mode == VideoMode.TextToVideo ? "/trainedAlgorithmicMedia" : "/compositeWithTrainedAlgorithmicMedia", video.C2paManifest);
            Assert.True(await db.MediaAssets.AnyAsync(m => m.ParentAssetId == video.Id && m.DerivationKey == MediaService.ThumbnailKey));
            Assert.InRange(MeanVolume(await f.Get<MediaService>(scope).ReadAsync(video, CancellationToken.None)), -60, -5);
        }
    }

    [Fact]
    public async Task Generative_video_requests_are_validated()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var videos = f.Get<VideoService>(scope);
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(
            new VideoJobRequest("ラテ", [Guid.NewGuid()], Mode: VideoMode.TextToVideo), CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(
            new VideoJobRequest("ラテ", [], Mode: VideoMode.ImageToVideo), CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(
            new VideoJobRequest("ラテ", [], Mode: VideoMode.TextToVideo, BgmTrackId: "unknown"), CancellationToken.None));
    }
}
