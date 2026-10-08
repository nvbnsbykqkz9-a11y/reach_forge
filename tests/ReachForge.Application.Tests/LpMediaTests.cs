using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using ReachForge.Infrastructure.Media;
using SkiaSharp;

namespace ReachForge.Application.Tests;

/// <summary>
/// LP からの広告の画像・動画：特色のある画像を選ぶ → 画像ごとに AI の広告写真（キービジュアル）→ SNS の形式ちょうどの画像 → 縦型・横型の動画。
/// </summary>
public class LpMediaTests
{
    private static readonly Uri Lp = new("https://example.com/lp/latte");

    private static WebPage Page => new(Lp, "秋限定さつまいもラテ | ほっこりカフェ", "10月末までの期間限定。",
        "秋限定さつまいもラテ 680円。焼きいもの香ばしさとミルクのやさしい甘さ。", ["#B45309"],
        [
            new WebImage(new Uri("https://example.com/img/latte.png"), "さつまいもラテ"),
            new WebImage(new Uri("https://example.com/img/shop.png"), "お店の外観"),
            new WebImage(new Uri("https://example.com/img/icon.png"), "アイコン"),        // 小さい（候補から除く）
            new WebImage(new Uri("https://example.com/img/banner.png"), "細長いバナー"),   // 細長い（候補から除く）
            new WebImage(new Uri("https://example.com/img/missing.jpg"), "取得できない画像"),
        ]);

    private sealed class FakeFetcher : IWebPageFetcher
    {
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => Task.FromResult(Page);

        public async Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct)
        {
            if (url.AbsolutePath.Contains("missing", StringComparison.Ordinal)) throw new DomainException(ErrorCodes.BrdUrlUnavailable, "取得できません");
            var (w, h) = url.AbsolutePath switch
            {
                var p when p.Contains("icon") => (64, 64),
                var p when p.Contains("banner") => (1600, 200),
                var p when p.Contains("shop") => (1200, 800),
                _ => (1000, 1200),
            };
            var image = await new SkiaImageProcessor().RenderPlaceholderAsync(w, h, url.AbsolutePath.Length, ["#B45309", "#FDE68A"], ct);
            return new FetchedImage(image.Bytes, image.Mime);
        }
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
    public async Task Preview_recommends_distinctive_images_and_skips_icons_banners_and_broken_ones()
    {
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new FakeFetcher()));
        await using var scope = f.Scope();
        var preview = await f.Get<LpStudioService>(scope).PreviewAsync(Lp.ToString(), CancellationToken.None);

        Assert.Equal(["https://example.com/img/latte.png", "https://example.com/img/shop.png"], preview.Images.Select(i => i.Url));
        Assert.Equal([1, 2], preview.Images.Select(i => i.Recommended));
        Assert.All(preview.Images, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
        Assert.Equal((1000, 1200), (preview.Images[0].Width, preview.Images[0].Height));
    }

    [Fact]
    public void Each_platform_has_its_own_image_and_video_formats()
    {
        var (images, videos) = LpMediaService.FormatsFor([SocialPlatform.Instagram, SocialPlatform.X, SocialPlatform.YouTube, SocialPlatform.Line], true);
        Assert.Contains(images, x => x is { Platform: SocialPlatform.Instagram, Format: { Width: 1080, Height: 1350 } });
        Assert.Contains(images, x => x is { Platform: SocialPlatform.Instagram, Format: { Width: 1080, Height: 1920 } });
        Assert.Contains(images, x => x is { Platform: SocialPlatform.X, Format: { Width: 1200, Height: 1200 } });
        Assert.Contains(images, x => x is { Platform: SocialPlatform.YouTube, Format: { Width: 1280, Height: 720 } });
        Assert.Contains(images, x => x is { Platform: SocialPlatform.Line, Format: { Width: 1200, Height: 628 } });
        Assert.Contains(videos, x => x is { Platform: SocialPlatform.YouTube, Format.Orientation: MediaOrientation.Landscape });
        Assert.Contains(videos, x => x is { Platform: SocialPlatform.Instagram, Format.Orientation: MediaOrientation.Portrait, Format.MaxSeconds: 90 });
        Assert.All(PlatformCatalog.InitialRelease.Where(c => c.Platform != SocialPlatform.LinkedIn && c.Platform != SocialPlatform.Pinterest),
            c => Assert.True(c.ImageFormats.Count > 0 && c.VideoFormats.Count > 0, c.DisplayName));
        Assert.Equal("1.91:1", new AspectRatio(191, 100).ToString());
    }

    [Fact]
    public async Task Creates_ai_key_visuals_exact_size_platform_images_and_vertical_and_horizontal_videos()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new FakeFetcher()));
        Guid projectId, jobId;
        await using (var scope = f.Scope())
        {
            var project = await f.Get<LpStudioService>(scope).CreateAsync(new LpProjectRequest
            {
                Url = Lp.ToString(), Platforms = [SocialPlatform.Instagram, SocialPlatform.YouTube],
                ImageUrls = ["https://example.com/img/latte.png", "https://example.com/img/shop.png"],
                ImageDescriptions = new Dictionary<string, string> { ["https://example.com/img/latte.png"] = "湯気の立つさつまいもラテ" },
                RightsConfirmed = true, MakeVideo = true, VideoSeconds = 15, Narration = true,
            }, CancellationToken.None);
            projectId = project.Id;
            jobId = project.MediaJobId!.Value;
            Assert.Equal("湯気の立つさつまいもラテ", project.Sources[0].Description);
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
            var project = await db.LpProjects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            var media = f.Get<MediaService>(scope);

            // 素材画像ごとのビジュアル案と、必要な向き（縦長：フィード・ストーリーズ・ショート、横長：サムネイル・横型動画）のキービジュアル
            Assert.Equal(2, project.Visuals.Count);
            Assert.All(project.Visuals, v => Assert.Equal([MediaOrientation.Portrait, MediaOrientation.Landscape], v.KeyVisuals.Keys.Order()));
            var keyVisual = await media.GetAsync(project.Visuals[0].KeyVisuals[MediaOrientation.Portrait], CancellationToken.None);
            Assert.True(keyVisual.IsAiLabeled);
            Assert.Contains("lp-key-visual", keyVisual.Provenance);

            // SNS の形式ちょうどの大きさ（素材2枚 × 形式）
            var instagram = project.Outputs[SocialPlatform.Instagram];
            Assert.Equal(4, instagram.Images.Count);
            foreach (var image in instagram.Images.Concat(project.Outputs[SocialPlatform.YouTube].Images))
            {
                var asset = await media.GetAsync(image.AssetId, CancellationToken.None);
                Assert.Equal((image.Width, image.Height), (asset.Width, asset.Height));
                Assert.Equal("image/jpeg", asset.Mime);
            }
            Assert.Contains(instagram.Images, i => i is { Key: "feed", Width: 1080, Height: 1350 });
            Assert.Contains(instagram.Images, i => i is { Key: "story", Width: 1080, Height: 1920 });
            Assert.Contains(project.Outputs[SocialPlatform.YouTube].Images, i => i is { Key: "thumbnail", Width: 1280, Height: 720 });

            // 縦型・横型の動画（枠ちょうど）を、SNS の形式に割り当てる
            var portrait = await media.GetAsync(project.Videos[MediaOrientation.Portrait], CancellationToken.None);
            var landscape = await media.GetAsync(project.Videos[MediaOrientation.Landscape], CancellationToken.None);
            Assert.Equal((1080, 1920), (portrait.Width, portrait.Height));
            Assert.Equal((1920, 1080), (landscape.Width, landscape.Height));
            Assert.InRange(landscape.DurationMs!.Value, 10_000, 40_000);
            Assert.Equal(portrait.Id, instagram.Videos.Single().AssetId);
            Assert.Equal([portrait.Id, landscape.Id], project.Outputs[SocialPlatform.YouTube].Videos.Select(v => v.AssetId));
            Assert.Contains("\"generated\":true", landscape.Provenance); // 動画生成 AI で動かしたシーンがある
            Assert.Equal(100, job.ProgressPercent);

            // 確認用：RF_TEST_DUMP を指定すると、できた画像・動画を書き出す
            if (Environment.GetEnvironmentVariable("RF_TEST_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                foreach (var (name, id) in new[] { ("portrait.mp4", portrait.Id), ("landscape.mp4", landscape.Id) }
                             .Concat(instagram.Images.Select((x, i) => ($"ig-{x.Key}-{i}.jpg", x.AssetId)))
                             .Concat(project.Outputs[SocialPlatform.YouTube].Images.Select((x, i) => ($"yt-{x.Key}-{i}.jpg", x.AssetId))))
                {
                    await File.WriteAllBytesAsync(Path.Combine(dump, name), await media.ReadAsync(await media.GetAsync(id, CancellationToken.None), CancellationToken.None));
                }
            }
        }
    }

    /// <summary>SaaS の LP：画面（ダッシュボード・スマホアプリ）の画像だけがある。</summary>
    private static WebPage SaasPage => new(new Uri("https://example.com/lp/secureview"), "SecureView | 検知から初動まで1画面で", "",
        "アラートが多すぎる。AI が検知・分析し、対応が必要なものだけを知らせます。", ["#1F4FD8", "#0B1B3A"],
        [
            new WebImage(new Uri("https://example.com/img/dashboard.png"), "ダッシュボードの画面"),
            new WebImage(new Uri("https://example.com/img/app.png"), "スマホアプリの画面"),
        ]);

    private sealed class SaasFetcher : IWebPageFetcher
    {
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => Task.FromResult(SaasPage);

        public async Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct)
        {
            var (w, h) = url.AbsolutePath.Contains("app") ? (750, 1500) : (1600, 1000);
            var image = await new SkiaImageProcessor().RenderPlaceholderAsync(w, h, url.AbsolutePath.Length, ["#F4F7FC", "#1F4FD8"], ct);
            return new FetchedImage(image.Bytes, image.Mime);
        }
    }

    /// <summary>明るさのばらつき（標準偏差）。無地の画像は 0 に近い。</summary>
    private static double Contrast(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        var values = new List<double>();
        for (var y = 0; y < bitmap.Height; y += 8)
        {
            for (var x = 0; x < bitmap.Width; x += 8)
            {
                var c = bitmap.GetPixel(x, y);
                values.Add(0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue);
            }
        }
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    [Fact]
    public async Task Saas_screens_are_placed_in_devices_on_a_backdrop_from_the_lp()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new SaasFetcher()));
        Guid projectId, jobId;
        await using (var scope = f.Scope())
        {
            var studio = f.Get<LpStudioService>(scope);
            var preview = await studio.PreviewAsync(SaasPage.Url.ToString(), CancellationToken.None);
            Assert.All(preview.Images, i => Assert.Equal(LpSourceKind.Screen, i.Kind)); // 画面も素材として選ぶ
            var project = await studio.CreateAsync(new LpProjectRequest
            {
                Url = SaasPage.Url.ToString(), Platforms = [SocialPlatform.Instagram],
                ImageUrls = [.. preview.Images.Select(i => i.Url)],
                ImageKinds = preview.Images.ToDictionary(i => i.Url, i => i.Kind),
                RightsConfirmed = true, MakeVideo = true, VideoSeconds = 15, Narration = false,
            }, CancellationToken.None);
            projectId = project.Id;
            jobId = project.MediaJobId!.Value;
            Assert.All(project.Sources, x => Assert.Equal(LpSourceKind.Screen, x.Kind));
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
            var project = await db.LpProjects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            var media = f.Get<MediaService>(scope);

            // 世界観は LP の色から
            Assert.Contains("#1F4FD8", project.Direction.Palette);
            Assert.False(string.IsNullOrWhiteSpace(project.Direction.Setting));
            Assert.All(project.Visuals, v => Assert.Equal(LpSourceKind.Screen, v.Kind));
            var keyVisual = await media.GetAsync(project.Visuals[0].KeyVisuals[MediaOrientation.Portrait], CancellationToken.None);
            Assert.Contains("lp-key-visual-screen", keyVisual.Provenance);

            var instagram = project.Outputs[SocialPlatform.Instagram];
            foreach (var image in instagram.Images)
            {
                var asset = await media.GetAsync(image.AssetId, CancellationToken.None);
                Assert.Equal((image.Width, image.Height), (asset.Width, asset.Height));
                Assert.True(Contrast(await media.ReadAsync(asset, CancellationToken.None)) > 12, image.Description);
            }
            var video = await media.GetAsync(project.Videos[MediaOrientation.Portrait], CancellationToken.None);
            Assert.Equal((1080, 1920), (video.Width, video.Height));
            Assert.Contains("\"generated\":true", video.Provenance); // 背景の映像を動画生成 AI でつくり、画面を重ねた

            if (Environment.GetEnvironmentVariable("RF_TEST_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                await File.WriteAllBytesAsync(Path.Combine(dump, "saas-portrait.mp4"), await media.ReadAsync(video, CancellationToken.None));
                foreach (var (x, i) in instagram.Images.Select((x, i) => (x, i)))
                {
                    await File.WriteAllBytesAsync(Path.Combine(dump, $"saas-ig-{x.Key}-{i}.jpg"),
                        await media.ReadAsync(await media.GetAsync(x.AssetId, CancellationToken.None), CancellationToken.None));
                }
            }
        }
    }

    [Fact]
    public async Task Without_image_ai_or_lp_images_the_ad_is_never_a_flat_color()
    {
        await using var f = await AppFixture.CreateAsync(new Dictionary<string, string?>
        {
            ["AI:Routes:Image:0"] = "none",
            ["AI:Routes:ImageEdit:0"] = "none",
        }, configure: s => s.AddSingleton<IWebPageFetcher>(new SaasFetcher()));
        Guid projectId, jobId;
        await using (var scope = f.Scope())
        {
            var project = await f.Get<LpStudioService>(scope).CreateAsync(new LpProjectRequest
            {
                Url = SaasPage.Url.ToString(), Platforms = [SocialPlatform.X], ImageUrls = [], MakeVideo = false,
            }, CancellationToken.None);
            projectId = project.Id;
            jobId = project.MediaJobId!.Value;
        }
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }
        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var project = await db.LpProjects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            var media = f.Get<MediaService>(scope);
            Assert.True(project.Visuals.Single().Fallback);
            var image = project.Outputs[SocialPlatform.X].Images.First();
            var bytes = await media.ReadAsync(await media.GetAsync(image.AssetId, CancellationToken.None), CancellationToken.None);
            Assert.True(Contrast(bytes) > 8); // LP の色で模様を描いた背景（無地ではない）
        }
    }
}
