using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Media;
using SkiaSharp;

namespace ReachForge.Application.Tests;

/// <summary>AI 生成物の来歴（F-04 処理 4：C2PA、11章：SNS の AI ラベル）。</summary>
public class ProvenanceTests
{
    private static readonly ProvenanceInfo Info = new(DigitalSourceType.TrainedAlgorithmicMedia, "c2pa.created", "openai", "gpt-image-1",
        new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), "ai-1.jpg");

    private static byte[] Encode(bool png)
    {
        return TestImages.Create(64, 48, new SKColor(10, 120, 200), format: png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg);
    }

    private static C2paProvenanceStamper Stamper(C2paOptions? c2pa = null) =>
        new(Options.Create(new MediaOptions { C2pa = c2pa ?? new C2paOptions() }), NullLogger<C2paProvenanceStamper>.Instance);

    [Theory]
    [InlineData(false, "image/jpeg")]
    [InlineData(true, "image/png")]
    public async Task Iptc_digital_source_type_is_embedded_without_reencoding(bool png, string mime)
    {
        var original = Encode(png);
        var stamped = await Stamper().StampAsync(original, mime, Info, CancellationToken.None);

        Assert.False(stamped.Signed);
        Assert.True(stamped.Bytes.Length > original.Length);
        Assert.Equal(TestImages.Read(original)[5, 5], TestImages.Read(stamped.Bytes)[5, 5]); // 画素は再エンコードしない
        var xmp = TestImages.Xmp(stamped.Bytes)!;
        Assert.Contains("Iptc4xmpExt:DigitalSourceType=\"http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia\"", xmp);

        var manifest = JsonNode.Parse(stamped.Manifest)!;
        var action = manifest["assertions"]![0]!["data"]!["actions"]![0]!;
        Assert.Equal("c2pa.created", action["action"]!.GetValue<string>());
        Assert.Contains("gpt-image-1", action["softwareAgent"]!.GetValue<string>());
        Assert.Equal("notAllowed", manifest["assertions"]![1]!["data"]!["entries"]!["c2pa.ai_generative_training"]!["use"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configured_c2patool_signs_and_embeds_the_manifest()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("rf-c2pa-test-");
        try
        {
            // c2patool の代わり：引数を確認し、入力の後ろにマニフェストを付けて出力する
            var tool = Path.Combine(dir.FullName, "c2patool");
            await File.WriteAllTextAsync(tool, """
                #!/bin/sh
                [ "$2" = "--manifest" ] && [ "$4" = "--output" ] && [ "$6" = "--force" ] || exit 2
                grep -q '"private_key"' "$3" || exit 3
                cat "$1" "$3" > "$5"
                """);
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var cert = Path.Combine(dir.FullName, "cert.pem");
            var key = Path.Combine(dir.FullName, "key.pem");
            await File.WriteAllTextAsync(cert, "cert");
            await File.WriteAllTextAsync(key, "key");

            var stamped = await Stamper(new C2paOptions { ToolPath = tool, SignCertPath = cert, PrivateKeyPath = key })
                .StampAsync(Encode(false), "image/jpeg", Info, CancellationToken.None);
            Assert.True(stamped.Signed);
            Assert.Contains("\"sign_cert\"", Encoding.UTF8.GetString(stamped.Bytes));
            Assert.DoesNotContain("private_key", stamped.Manifest); // DB に残すマニフェストに鍵の場所は入れない

            // 署名に失敗しても XMP は付ける
            var failed = await Stamper(new C2paOptions { ToolPath = "/nonexistent/c2patool", SignCertPath = cert, PrivateKeyPath = key })
                .StampAsync(Encode(false), "image/jpeg", Info, CancellationToken.None);
            Assert.False(failed.Signed);
            Assert.NotNull(TestImages.Xmp(failed.Bytes));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Generated_images_and_their_sns_versions_keep_the_ai_provenance()
    {
        await using var f = await AppFixture.CreateAsync();
        Guid jobId;
        await using (var scope = f.Scope())
        {
            jobId = (await f.Get<MediaService>(scope).EnqueueGenerationAsync(new ImageJobRequest { Prompt = "ラテ" }, CancellationToken.None)).Id;
        }
        await using (var scope = f.Scope())
        {
            await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
        }
        await using (var scope = f.Scope())
        {
            var media = f.Get<MediaService>(scope);
            var job = await media.GetJobAsync(jobId, CancellationToken.None);
            var asset = await f.Get<IAppDbContext>(scope).MediaAssets.AsNoTracking().SingleAsync(a => a.Id == job.ResultAssetIds[0]);
            Assert.Contains("trainedAlgorithmicMedia", asset.C2paManifest);
            Assert.NotNull(TestImages.Xmp(await media.ReadAsync(asset, CancellationToken.None)));

            // SNS 用に比率を変えた画像（作り直すため来歴は消える）にも付け直す
            var derived = await media.DeriveForPlatformAsync(asset, SocialPlatform.X, AspectMethod.Pad, CancellationToken.None);
            await f.Get<IAppDbContext>(scope).SaveChangesAsync();
            Assert.Contains("c2pa.resized", derived.C2paManifest);
            var xmp = TestImages.Xmp(await media.ReadAsync(derived, CancellationToken.None))!;
            Assert.Contains("trainedAlgorithmicMedia", xmp);
        }
    }
}
