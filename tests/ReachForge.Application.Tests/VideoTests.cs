using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
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
}
