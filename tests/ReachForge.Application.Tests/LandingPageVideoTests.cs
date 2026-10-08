using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Media;
using ReachForge.Infrastructure.Web;

namespace ReachForge.Application.Tests;

public class LandingPageVideoTests
{
    private static readonly Uri Lp = new("https://example.com/lp/latte");

    private static WebPage Page => new(Lp, "秋限定さつまいもラテ | ほっこりカフェ", "10月末までの期間限定。",
        "秋限定さつまいもラテ 680円。焼きいもの香ばしさとミルクのやさしい甘さ。\nご来店の方にクッキーをプレゼント。", ["#B45309"],
        [
            new WebImage(new Uri("https://example.com/og.jpg"), null, IsShareImage: true),
            new WebImage(new Uri("https://example.com/img/latte.png"), "さつまいもラテ"),
            new WebImage(new Uri("https://example.com/img/missing.jpg"), "取得できない画像"),
        ]);

    /// <summary>LP と画像を返すテスト用の取得（missing.jpg は取得できない）。</summary>
    private sealed class FakeLpFetcher : IWebPageFetcher
    {
        public List<Uri> ImageRequests { get; } = [];

        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => Task.FromResult(Page);

        public async Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct)
        {
            ImageRequests.Add(url);
            if (url.AbsolutePath.Contains("missing", StringComparison.Ordinal))
            {
                throw new DomainException(ErrorCodes.BrdUrlUnavailable, "取得できません");
            }
            var image = await new ImageSharpProcessor().RenderPlaceholderAsync(1200, 900, url.AbsolutePath.Length, ["#B45309", "#FDE68A"], ct);
            return new FetchedImage(image.Bytes, image.Mime);
        }
    }

    private sealed class Recorder(System.Collections.Concurrent.ConcurrentQueue<JobProgressEvent> events) : IRealtimeNotifier
    {
        public void Publish(RealtimeEvent e)
        {
            if (e is JobProgressEvent job) events.Enqueue(job);
        }
    }

    [Fact]
    public async Task Creating_from_a_landing_page_reports_each_step_in_order()
    {
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new FakeLpFetcher()));
        await using var scope = f.Scope();
        var request = new LpProjectRequest
        {
            Url = Lp.ToString(), Platforms = [SocialPlatform.Instagram, SocialPlatform.YouTube],
            ImageUrls = ["https://example.com/og.jpg", "https://example.com/img/latte.png"], RightsConfirmed = true, MakeVideo = false,
        };
        var steps = LpStudioService.CreateSteps(request);
        Assert.Equal(["LP を読み込む", "LP の画像を取り込む", "Instagram：広告文・投稿文・画像をつくる", "YouTube：広告文・投稿文をつくる"], steps);

        var reports = new List<LpCreateProgress>();
        await f.Get<LpStudioService>(scope).CreateAsync(request, CancellationToken.None, new SyncProgress(reports.Add));
        Assert.Equal(
            [new(0), new(1, "1/2枚"), new(1, "2/2枚"), new(2), new(3), new(4)],
            reports);
        Assert.Equal(steps.Count, reports[^1].StepIndex); // 最後は「すべて終わった」
    }

    private sealed class FailingTts : ITextToSpeech
    {
        public int Calls;

        public Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct)
        {
            Calls++;
            throw new AiUnavailableException("ナレーションを作れませんでした：OpenAI の API キーが正しくないか、権限がありません。");
        }
    }

    [Fact]
    public async Task Video_is_finished_without_narration_when_speech_fails()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        var tts = new FailingTts();
        await using var f = await AppFixture.CreateAsync(configure: s => s
            .AddSingleton<IWebPageFetcher>(new FakeLpFetcher())
            .AddSingleton<ITextToSpeech>(tts));
        Guid jobId;
        await using (var scope = f.Scope())
        {
            jobId = (await f.Get<VideoService>(scope).EnqueueAsync(new VideoJobRequest("", [], Narration: true, TargetSeconds: 15,
                Mode: VideoMode.LandingPage, SourceUrl: Lp.ToString()), CancellationToken.None)).Id;
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
            Assert.Equal(1, tts.Calls); // 1回失敗したら、ほかのシーンでは呼ばない
            var video = await db.MediaAssets.SingleAsync(m => m.Id == job.ResultAssetIds[0]);
            var summary = VideoService.LandingPageSummaryOf(video)!;
            Assert.Contains("API キーが正しくない", summary.NarrationError);
            Assert.Contains("\"narration\":false", video.Provenance);
        }
    }

    private sealed class SyncProgress(Action<LpCreateProgress> report) : IProgress<LpCreateProgress>
    {
        public void Report(LpCreateProgress value) => report(value);
    }

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
    public async Task Landing_page_video_uses_lp_images_animates_the_hook_and_suggests_a_post()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        var fetcher = new FakeLpFetcher();
        var events = new System.Collections.Concurrent.ConcurrentQueue<JobProgressEvent>();
        await using var f = await AppFixture.CreateAsync(configure: s => s
            .AddSingleton<IWebPageFetcher>(fetcher)
            .AddSingleton<IRealtimeNotifier>(new Recorder(events)));
        Guid jobId;
        await using (var scope = f.Scope())
        {
            var videos = f.Get<VideoService>(scope);
            var preview = await videos.PreviewLandingPageAsync(Lp.ToString(), CancellationToken.None);
            Assert.Equal(3, preview.ImageList.Count);

            var request = new VideoJobRequest("初回の特典を中心に", [], Narration: true, TargetSeconds: 15, Mode: VideoMode.LandingPage,
                BgmTrackId: "calm", SourceUrl: Lp.ToString(), SourceImageUrls: preview.ImageList.Select(i => i.Url.ToString()).ToList(),
                AnimateHook: true, RightsConfirmed: true);
            var job = await videos.EnqueueAsync(request, CancellationToken.None);
            jobId = job.Id;
        }
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
            Assert.Equal(100, job.ProgressPercent);

            // 進み具合：LP の読み込み → 画像の取り込み → 構成 → シーンごと → 書き出し の順に、割合が増えながら画面へ届く
            var progress = events.Where(e => e.JobId == jobId && e.Text is not null).ToList();
            Assert.Equal(progress.Select(e => e.Percent).Order(), progress.Select(e => e.Percent));
            Assert.Equal("LP を読み込んでいます", progress[0].Text);
            Assert.Contains(progress, e => e.Text == "LP の画像を取り込んでいます（3/3）");
            Assert.Contains(progress, e => e.Text!.StartsWith("AI が動画の構成", StringComparison.Ordinal));
            Assert.Contains(progress, e => e.Text!.StartsWith("シーン 1/", StringComparison.Ordinal) && e.Text.Contains("ナレーション"));
            Assert.Contains(progress, e => e.Text!.StartsWith("冒頭のシーンを AI で動かしています", StringComparison.Ordinal));
            Assert.Contains(progress, e => e.Text!.StartsWith("BGM を重ねて", StringComparison.Ordinal) && e.Stage == AiJobStage.Checking);
            var video = await db.MediaAssets.SingleAsync(m => m.Id == job.ResultAssetIds[0]);
            Assert.Equal((MediaKind.Video, 1080, 1920), (video.Kind, video.Width, video.Height));
            Assert.InRange(video.DurationMs!.Value, 10_000, 31_000);
            Assert.True(video.IsAiLabeled);
            Assert.StartsWith("1\n00:00:00,000 --> ", video.SubtitlesSrt);

            var summary = VideoService.LandingPageSummaryOf(video);
            Assert.NotNull(summary);
            Assert.Equal(Lp.ToString(), summary.Url);
            Assert.True(summary.HookAnimated);
            Assert.False(string.IsNullOrWhiteSpace(summary.PostText));

            // 取得できた LP の画像はライブラリに取り込み、取得元を来歴に残す（取得できない画像は飛ばす）
            var imported = await db.MediaAssets.Where(m => m.Provenance != null && m.Provenance.Contains("\"kind\":\"web\"")).ToListAsync();
            Assert.Equal(2, imported.Count);
            Assert.Contains(imported, m => m.AltText == "さつまいもラテ");
            Assert.Equal(3, fetcher.ImageRequests.Count);
        }
    }

    [Fact]
    public async Task Hook_falls_back_to_a_still_when_generation_fails_and_is_not_charged()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync(configure: s =>
        {
            s.AddSingleton<IWebPageFetcher>(new FakeLpFetcher());
            s.AddScoped<IVideoGenerationService, FailingGenerator>();
        });
        Guid jobId;
        await using (var scope = f.Scope())
        {
            var job = await f.Get<VideoService>(scope).EnqueueAsync(new VideoJobRequest("", [], Narration: false, TargetSeconds: 10,
                Mode: VideoMode.LandingPage, SourceUrl: Lp.ToString(), SourceImageUrls: ["https://example.com/og.jpg"],
                AnimateHook: true, RightsConfirmed: true), CancellationToken.None);
            jobId = job.Id;
        }
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }
        await using (var scope = f.Scope())
        {
            var job = await f.Get<IAppDbContext>(scope).AiJobs.SingleAsync(j => j.Id == jobId);
            Assert.True(job.Status == AiJobStatus.Succeeded, job.Error);
            var video = await f.Get<IAppDbContext>(scope).MediaAssets.SingleAsync(m => m.Id == job.ResultAssetIds[0]);
            Assert.False(VideoService.LandingPageSummaryOf(video)!.HookAnimated);
        }
    }

    private sealed class FailingGenerator : IVideoGenerationService
    {
        public Task<GeneratedVideo> GenerateAsync(VideoGenerationSpec spec, Guid? generationId, CancellationToken ct) =>
            throw new AiUnavailableException("動画の生成 AI が利用できません");
    }

    [Fact]
    public async Task Landing_page_requests_are_validated()
    {
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new FakeLpFetcher()));
        await using var scope = f.Scope();
        var videos = f.Get<VideoService>(scope);
        var ok = new VideoJobRequest("", [], Mode: VideoMode.LandingPage, SourceUrl: Lp.ToString());
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(ok with { SourceUrl = "ftp://example.com" }, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(ok with { ImageAssetIds = [Guid.NewGuid()] }, CancellationToken.None));
        // 画像を使うなら権利の確認が必要
        var withImages = ok with { SourceImageUrls = ["https://example.com/og.jpg"] };
        var ex = await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(withImages, CancellationToken.None));
        Assert.Contains("権利", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(ok with { AnimateHook = true }, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => videos.EnqueueAsync(
            withImages with { RightsConfirmed = true, SourceImageUrls = Enumerable.Range(0, 9).Select(i => $"https://example.com/{i}.jpg").ToList() },
            CancellationToken.None));
        // テーマ（伝えたいこと）は任意
        Assert.NotNull(await videos.EnqueueAsync(ok, CancellationToken.None));
    }

    [Fact]
    public void Page_images_put_share_image_first_and_skip_decorations()
    {
        const string html = """
            <html><head><title>LP</title>
            <meta property="og:image" content="/og.jpg">
            </head><body>
            <img src="/img/hero.webp" alt="店内の写真">
            <img src="/img/icon-cart.png" alt="">
            <img src="/img/logo.svg" alt="ロゴ">
            <img src="/img/tiny.jpg" width="40" height="40">
            <img src="data:image/png;base64,AAAA">
            <img data-src="https://cdn.example.com/menu.jpg" src="/img/placeholder-loading.jpg" alt="メニュー">
            <img src="/og.jpg">
            <script>var x = '<img src="/img/script.jpg">';</script>
            </body></html>
            """;
        var page = SafeWebPageFetcher.Parse(new Uri("https://example.com/lp/"), html);
        Assert.Equal(
            ["https://example.com/og.jpg", "https://example.com/img/hero.webp", "https://cdn.example.com/menu.jpg"],
            page.ImageList.Select(i => i.Url.ToString()));
        Assert.True(page.ImageList[0].IsShareImage);
        Assert.Equal("店内の写真", page.ImageList[1].Alt);
    }
}
