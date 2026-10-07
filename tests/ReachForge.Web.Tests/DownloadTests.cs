using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ReachForge.Web.Tests;

/// <summary>つくったもののダウンロード（1つずつ・まとめて ZIP）と、画面表示用の署名付き URL。</summary>
public class DownloadTests
{
    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(640, 640, new Rgba32(30, 140, 200));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>オーナーとして、画像1枚と Instagram 用の文章を持つ「つくったもの」を用意する。</summary>
    private static async Task<(Guid Project, Guid Image, string SignedUrl)> SeedAsync(WebFixture app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext
        {
            TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, UserName = "田中", Role = Role.Owner,
        };
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var png = Png();
        var source = await media.UploadAsync("latte.png", "image/png", new MemoryStream(png), png.Length, CancellationToken.None);
        var image = await media.DeriveForPlatformAsync(source, SocialPlatform.Instagram, AspectMethod.SmartCrop, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var project = new LpProject
        {
            TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, Url = "https://example.com/latte", Title = "秋限定ラテ",
            Platforms = [SocialPlatform.Instagram], Status = LpProjectStatus.Ready,
            Outputs = new()
            {
                [SocialPlatform.Instagram] = new LpPlatformOutput
                {
                    PostText = "秋限定ラテが登場しました。", Hashtags = ["カフェ"], ImageAssetIds = [image.Id],
                    AdCopies = [new LpAdCopy("さつまいもラテ、はじめました。", "秋限定ラテ", "", "LEARN_MORE")],
                },
            },
        };
        db.LpProjects.Add(project);
        await db.SaveChangesAsync();
        var url = await scope.ServiceProvider.GetRequiredService<IMediaUrlSigner>().CreateReadUrlAsync(image.Id, TimeSpan.FromMinutes(5), CancellationToken.None);
        return (project.Id, image.Id, url);
    }

    [Fact]
    public async Task Files_and_zip_are_downloadable_only_after_login()
    {
        await using var app = new WebFixture();
        app.CreateClient().Dispose();
        var (projectId, imageId, _) = await SeedAsync(app);

        var anonymous = app.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/files/{imageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/lp/{projectId}/download")).StatusCode);

        var client = app.Browser();
        await WebFixture.LoginAsync(client, "owner@example.com");
        var file = await client.GetAsync($"/api/v1/files/{imageId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);

        var zipResponse = await client.GetAsync($"/api/v1/lp/{projectId}/download");
        Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        using var zip = new ZipArchive(await zipResponse.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName == "Instagram/画像1.jpg" && e.Length > 0);
        var text = zip.Entries.Single(e => e.FullName == "Instagram/文章.txt");
        using var reader = new StreamReader(text.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Contains("秋限定ラテが登場しました。", content);
        Assert.Contains("#カフェ", content);
        Assert.Contains("見出し：秋限定ラテ", content);
        Assert.Contains("ボタン：詳しくはこちら", content);

        // 存在しないものは 404
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/lp/{Guid.NewGuid()}/download")).StatusCode);
    }

    [Fact]
    public async Task Signed_media_urls_work_without_login_and_reject_tampering()
    {
        await using var app = new WebFixture();
        app.CreateClient().Dispose();
        var (_, _, url) = await SeedAsync(app);

        var anonymous = app.Browser();
        var image = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.StartsWith("image/", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url[..^2] + "xx")).StatusCode);
    }
}
