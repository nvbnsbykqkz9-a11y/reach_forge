using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using ReachForge.Infrastructure.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReachForge.Application.Tests;

public class ImageProcessorTests
{
    private readonly ImageSharpProcessor _p = new();

    /// <summary>右側だけに細かい模様（被写体）がある横長画像。</summary>
    private static byte[] WideImageWithSubjectOnRight(int width = 1600, int height = 900)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(240, 240, 240));
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = width * 3 / 4 - 100; x < width - 20; x++)
                {
                    row[x] = ((x / 8 + y / 8) % 2 == 0) ? new Rgba32(200, 30, 30) : new Rgba32(20, 20, 160);
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Smart_crop_moves_toward_the_detailed_subject()
    {
        using var image = Image.Load<Rgba32>(WideImageWithSubjectOnRight());
        var rect = ImageSharpProcessor.SmartCropRectangle(image, 4.0 / 5);
        Assert.Equal(720, rect.Width);
        Assert.Equal(900, rect.Height);
        Assert.True(rect.X > 1600 / 2, $"crop x={rect.X} should be on the right half");
    }

    [Theory]
    [InlineData(AspectMethod.SmartCrop)]
    [InlineData(AspectMethod.Pad)]
    public async Task Aspect_conversion_hits_target_ratio_without_stretching(AspectMethod method)
    {
        var c = PlatformCatalog.Get(SocialPlatform.Instagram);
        var result = await _p.ConvertAspectAsync(WideImageWithSubjectOnRight(), c.ImageAspect, c.ImageSize, method, "#FFFFFF",
            CancellationToken.None);
        Assert.Equal(c.ImageAspect.Value, (double)result.Width / result.Height, 2);
        Assert.True(result.Width <= c.ImageSize.Width && result.Height <= c.ImageSize.Height);
    }

    [Fact]
    public async Task Small_images_are_not_upscaled()
    {
        var c = PlatformCatalog.Get(SocialPlatform.X);
        var result = await _p.ConvertAspectAsync(WideImageWithSubjectOnRight(320, 180), c.ImageAspect, c.ImageSize, AspectMethod.SmartCrop,
            "#FFFFFF", CancellationToken.None);
        Assert.Equal((320, 180), (result.Width, result.Height));
    }

    [Fact]
    public async Task Jpeg_encoding_respects_size_limit_and_flattens_transparency()
    {
        var canvas = await _p.PrepareOutpaintCanvasAsync(WideImageWithSubjectOnRight(), new AspectRatio(1, 1), (1080, 1080),
            CancellationToken.None);
        Assert.Equal("image/png", canvas.Mime);

        var jpeg = await _p.EncodeJpegAsync(canvas.Bytes, 60_000, CancellationToken.None);
        Assert.Equal("image/jpeg", jpeg.Mime);
        Assert.True(jpeg.Bytes.Length <= 60_000, $"{jpeg.Bytes.Length} bytes");
        Assert.Equal(0xFF, jpeg.Bytes[0]);
    }

    [Fact]
    public async Task Normalize_strips_metadata_and_auto_orients()
    {
        using var image = new Image<Rgba32>(400, 200, new Rgba32(10, 120, 200));
        image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6); // 90°回転
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software, "camera");
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        ms.Position = 0;

        var result = await _p.NormalizeAsync(ms, 4096, CancellationToken.None);

        Assert.Equal((200, 400), (result.Width, result.Height));
        using var reloaded = Image.Load(result.Bytes);
        Assert.Null(reloaded.Metadata.ExifProfile);
    }
}

public class MediaServiceTests
{
    private static async Task<byte[]> PngAsync(int w, int h)
    {
        var p = new ImageSharpProcessor();
        return (await p.RenderPlaceholderAsync(w, h, 42, ["#B45309", "#FDE68A"], CancellationToken.None)).Bytes;
    }

    private static async Task<MediaAsset> UploadAsync(AppFixture f, AsyncServiceScope scope, int w = 1600, int h = 900)
    {
        var bytes = await PngAsync(w, h);
        return await f.Get<MediaService>(scope).UploadAsync("latte.png", "image/png", new MemoryStream(bytes), bytes.Length,
            CancellationToken.None);
    }

    [Fact]
    public async Task Adds_japanese_text_as_new_library_image()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var media = f.Get<MediaService>(scope);
        var source = await UploadAsync(f, scope);
        await Assert.ThrowsAsync<DomainException>(() => media.AddTextAsync(source.Id,
            new TextOverlay(new string('長', 31), null, TextPosition.Bottom, "#1B2333"), CancellationToken.None));

        MediaAsset banner;
        try
        {
            banner = await media.AddTextAsync(source.Id, new TextOverlay("秋限定 さつまいもラテ", "10/10（土）から販売", TextPosition.Bottom, "#1B2333"),
                CancellationToken.None);
        }
        catch (DomainException ex) when (ex.Message.Contains("日本語フォント"))
        {
            Assert.Skip("この環境には日本語フォントがありません");
            return;
        }
        Assert.Equal((MediaSource.Derived, source.Id, source.Width, source.Height), (banner.Source, banner.ParentAssetId, banner.Width, banner.Height));
        Assert.Contains("秋限定 さつまいもラテ", banner.AltText);
        Assert.Contains(await media.ListAsync(MediaFilter.All, CancellationToken.None), m => m.Id == banner.Id); // ライブラリに出る

        // 下部の帯に白い文字が描かれている（帯は暗い色）
        using var image = Image.Load<Rgba32>(await media.ReadAsync(banner, CancellationToken.None));
        var bright = 0;
        for (var x = 0; x < image.Width; x += 2)
        {
            for (var y = image.Height * 85 / 100; y < image.Height; y += 2)
            {
                var p = image[x, y];
                if (p.R > 220 && p.G > 220 && p.B > 220) bright++;
            }
        }
        Assert.True(bright > 100, $"text pixels: {bright}");
    }

    /// <summary>Worker の AiJobDispatcher と同じく、ジョブのテナントのコンテキストで1件実行する。</summary>
    private static async Task RunJobAsync(AppFixture f, Guid jobId)
    {
        await using var scope = f.Scope();
        await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
    }

    [Fact]
    public async Task Upload_rejects_unsupported_types_and_stores_normalized_image()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var media = f.Get<MediaService>(scope);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            media.UploadAsync("a.gif", "image/gif", new MemoryStream([1, 2, 3]), 3, CancellationToken.None));
        Assert.Contains("JPEG", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() =>
            media.UploadAsync("broken.png", "image/png", new MemoryStream([1, 2, 3]), 3, CancellationToken.None));

        var asset = await UploadAsync(f, scope);
        Assert.Equal((MediaSource.Upload, 1600, 900), (asset.Source, asset.Width, asset.Height));
        Assert.True(File.Exists(Path.Combine(f.MediaPath, asset.BlobPath)));
        Assert.Single(await media.ListAsync(MediaFilter.Uploaded, CancellationToken.None));
    }

    [Fact]
    public async Task Derivatives_match_platform_specs_and_are_reused()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var media = f.Get<MediaService>(scope);
        var asset = await UploadAsync(f, scope);

        var ig = await media.DeriveForPlatformAsync(asset, SocialPlatform.Instagram, AspectMethod.SmartCrop, CancellationToken.None);
        await f.Get<IAppDbContext>(scope).SaveChangesAsync();
        Assert.Equal("image/jpeg", ig.Mime);
        Assert.Equal(4.0 / 5, (double)ig.Width!.Value / ig.Height!.Value, 2);
        Assert.True(ig.Bytes <= MediaService.InstagramMaxBytes);
        Assert.Equal(asset.Id, ig.ParentAssetId);

        var again = await media.DeriveForPlatformAsync(asset, SocialPlatform.Instagram, AspectMethod.SmartCrop, CancellationToken.None);
        Assert.Equal(ig.Id, again.Id);
        // 派生画像は一覧に出さない
        Assert.Single(await media.ListAsync(MediaFilter.All, CancellationToken.None));
    }

    [Fact]
    public async Task Image_generation_job_charges_per_image_and_labels_as_ai()
    {
        await using var f = await AppFixture.CreateAsync();
        AiJob job;
        await using (var scope = f.Scope())
        {
            job = await f.Get<MediaService>(scope).EnqueueGenerationAsync(
                new ImageJobRequest { Prompt = "木のテーブルに置かれた湯気の立つさつまいもラテ", Count = 2 }, CancellationToken.None);
            var account = await f.Get<IAppDbContext>(scope).CreditAccounts.SingleAsync();
            Assert.Equal(10, account.Held); // 5 クレジット × 2 枚を予約
        }

        await RunJobAsync(f, job.Id);

        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var done = await db.AiJobs.SingleAsync(j => j.Id == job.Id);
            Assert.Equal(AiJobStatus.Succeeded, done.Status);
            Assert.Equal(2, done.ResultAssetIds.Count);
            Assert.Equal(10, done.CreditsCharged);

            var assets = await db.MediaAssets.Where(m => done.ResultAssetIds.Contains(m.Id)).ToListAsync();
            Assert.All(assets, a =>
            {
                Assert.True(a.IsAiLabeled);
                Assert.Equal(MediaSource.AiGenerated, a.Source);
                Assert.Contains("\"aiGenerated\":true", a.Provenance);
                Assert.False(string.IsNullOrWhiteSpace(a.AltText));
                Assert.Equal(4.0 / 5, a.AspectRatio, 2); // Instagram 基準
            });
            var account = await db.CreditAccounts.SingleAsync();
            Assert.Equal((1490, 0), (account.Balance, account.Held));
            Assert.Contains(await db.AiUsageLogs.ToListAsync(), u => u.TaskType == AiTaskType.Image && u.Images == 2);
        }
    }

    private sealed class FailingGenerator : IImageGenerationService
    {
        public Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationSpec spec, Guid? generationId, CancellationToken ct) =>
            throw new AiUnavailableException("down");
    }

    private sealed class BlockAll : IImageSafetyChecker
    {
        public Task<SafetyVerdict> CheckAsync(byte[] image, string mime, CancellationToken ct) => Task.FromResult(new SafetyVerdict(true, "violence"));
    }

    [Theory]
    [InlineData("generator")]
    [InlineData("safety")]
    public async Task Failed_or_blocked_jobs_release_credits(string failure)
    {
        await using var f = await AppFixture.CreateAsync(configure: s =>
        {
            if (failure == "generator") s.AddScoped<IImageGenerationService, FailingGenerator>();
            else s.AddSingleton<IImageSafetyChecker, BlockAll>();
        });
        AiJob job;
        await using (var scope = f.Scope())
        {
            job = await f.Get<MediaService>(scope).EnqueueGenerationAsync(new ImageJobRequest { Prompt = "ラテ" }, CancellationToken.None);
        }
        await RunJobAsync(f, job.Id);

        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var done = await db.AiJobs.SingleAsync(j => j.Id == job.Id);
            Assert.Equal(AiJobStatus.Failed, done.Status);
            Assert.Equal(failure == "generator" ? ErrorCodes.AiUnavailable : ErrorCodes.AiSafetyBlocked, done.ErrorCode);
            var account = await db.CreditAccounts.SingleAsync();
            Assert.Equal((1500, 0), (account.Balance, account.Held));
            Assert.Empty(await db.MediaAssets.ToListAsync());
        }
    }

    [Fact]
    public async Task Outpaint_job_replaces_variant_image()
    {
        await using var f = await AppFixture.CreateAsync();
        Guid variantId, sourceId;
        await using (var scope = f.Scope())
        {
            var source = await UploadAsync(f, scope);
            sourceId = source.Id;
            var db = f.Get<IAppDbContext>(scope);
            var channel = await db.Channels.FirstAsync(c => c.Platform == SocialPlatform.Instagram);
            var post = new MasterPost { WorkspaceId = channel.WorkspaceId, Title = "t", CoreMessage = "本文" };
            db.MasterPosts.Add(post);
            var v = PostVariant.Create(post, channel, "本文", []);
            v.SetMedia([source.Id], requiresApproval: true);
            v.Submit();
            v.Approve();
            db.PostVariants.Add(v);
            await db.SaveChangesAsync();
            variantId = v.Id;
            var job = await f.Get<MediaService>(scope).EnqueueOutpaintAsync(
                new OutpaintJobRequest(source.Id, SocialPlatform.Instagram, v.Id), CancellationToken.None);
            await RunJobAsync(f, job.Id);
        }

        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var v = await db.PostVariants.SingleAsync(x => x.Id == variantId);
            var asset = await db.MediaAssets.SingleAsync(m => m.Id == v.MediaAssetIds[0]);
            Assert.Equal((MediaSource.AiEdited, sourceId), (asset.Source, asset.ParentAssetId!.Value));
            Assert.Equal(AspectMethod.Outpaint, v.AspectMethod);
            Assert.Equal(VariantStatus.Approved, v.Status); // 承認の流れは廃止したため、画像が変わっても再承認にしない
            Assert.Equal(1495, (await db.CreditAccounts.SingleAsync()).Balance);
        }
    }

    [Fact]
    public async Task Media_in_use_cannot_be_deleted()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var asset = await UploadAsync(f, scope);
        var db = f.Get<IAppDbContext>(scope);
        var channel = await db.Channels.FirstAsync();
        var post = new MasterPost { WorkspaceId = channel.WorkspaceId, Title = "t", CoreMessage = "本文" };
        db.MasterPosts.Add(post);
        var v = PostVariant.Create(post, channel, "本文", []);
        v.SetMedia([asset.Id], requiresApproval: false);
        db.PostVariants.Add(v);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Get<MediaService>(scope).DeleteAsync(asset.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Signed_media_urls_expire_and_reject_tampering()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var protection = f.Get<IDataProtectionProvider>(scope);
        var id = Guid.NewGuid();
        var url = await f.Get<IMediaUrlSigner>(scope).CreateReadUrlAsync(id, TimeSpan.FromMinutes(5), CancellationToken.None);
        var token = url.Split("/media/")[1];

        Assert.Equal(id, AppMediaUrlSigner.Validate(protection, token, f.Clock));
        Assert.Null(AppMediaUrlSigner.Validate(protection, token[..^2] + "xx", f.Clock));

        f.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(AppMediaUrlSigner.Validate(protection, token, f.Clock));
    }

    [Fact]
    public async Task Variants_get_platform_specific_images()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var asset = await UploadAsync(f, scope, 1200, 1200);
        var studio = f.Get<StudioService>(scope);
        var post = await studio.SaveMasterPostAsync(new SaveMasterPost
        {
            Title = "秋限定ラテ", Objective = PostObjective.Traffic, CoreMessage = "秋限定ラテが登場します。", MediaAssetIds = [asset.Id],
        }, CancellationToken.None);
        var channels = await f.Get<ChannelService>(scope).ListAsync(CancellationToken.None);
        var variants = await studio.GenerateVariantsAsync(post.Id, channels.Select(c => c.Id).ToList(), new VariantOptions(),
            CancellationToken.None);

        var db = f.Get<IAppDbContext>(scope);
        foreach (var v in variants)
        {
            var image = await db.MediaAssets.SingleAsync(m => m.Id == Assert.Single(v.MediaAssetIds));
            var c = PlatformCatalog.Get(v.Platform);
            Assert.Equal(c.ImageAspect.Value, image.AspectRatio, 2);
            Assert.Equal(asset.Id, image.ParentAssetId);
        }
        // LINE はプレビュー用のサムネイルも作る
        var line = variants.Single(v => v.Platform == SocialPlatform.Line);
        Assert.True(await db.MediaAssets.AnyAsync(m => m.ParentAssetId == line.MediaAssetIds[0] && m.DerivationKey == MediaService.ThumbnailKey));

        // 比率変換の方法を「余白」に変える → 別の派生画像になる
        var x = variants.Single(v => v.Platform == SocialPlatform.X);
        var before = x.MediaAssetIds[0];
        Assert.Null(await studio.ChangeAspectMethodAsync(x.Id, AspectMethod.Pad, CancellationToken.None));
        Assert.NotEqual(before, (await db.PostVariants.SingleAsync(v => v.Id == x.Id)).MediaAssetIds[0]);
        // 「AIで広げる」はジョブになる
        Assert.NotNull(await studio.ChangeAspectMethodAsync(x.Id, AspectMethod.Outpaint, CancellationToken.None));
    }
}
