using System.IO.Compression;
using System.Text;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Web.Api;

/// <summary>つくったもののダウンロード（1つずつ・まとめて ZIP）。ログインした利用者の、自分のワークスペースのものだけ。</summary>
public static class LpEndpoints
{
    public static void MapLpEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/files/{id:guid}", async (Guid id, MediaService media, IMediaStorage storage, ITenantContext tenant, CancellationToken ct) =>
        {
            var asset = await media.GetAsync(id, ct);
            if (asset.WorkspaceId != tenant.WorkspaceId) return Results.NotFound(); // 別のワークスペースのものは出さない
            return Results.Stream(await storage.OpenReadAsync(asset.BlobPath, ct), asset.Mime, FileName(asset), enableRangeProcessing: true);
        });

        api.MapGet("/lp/{id:guid}/download", async (Guid id, LpStudioService lp, MediaService media, IMediaStorage storage, CancellationToken ct) =>
        {
            var project = await lp.GetAsync(id, ct);
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                async Task AddFileAsync(string path, Guid assetId)
                {
                    var asset = await media.GetAsync(assetId, ct);
                    var entry = zip.CreateEntry(path, asset.Kind == MediaKind.Video ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                    await using var target = await entry.OpenAsync(ct);
                    await using var source = await storage.OpenReadAsync(asset.BlobPath, ct);
                    await source.CopyToAsync(target, ct);
                }

                if (await lp.VideoJobAsync(project, ct) is { Status: AiJobStatus.Succeeded, ResultAssetIds: [var video, ..] })
                {
                    await AddFileAsync("動画（縦型）.mp4", video);
                }
                foreach (var (platform, output) in project.Outputs.OrderBy(o => LpStudioService.Supported.ToList().IndexOf(o.Key)))
                {
                    var folder = PlatformCatalog.Get(platform).DisplayName;
                    var text = zip.CreateEntry($"{folder}/文章.txt", CompressionLevel.Fastest);
                    await using (var writer = new StreamWriter(await text.OpenAsync(ct), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                    {
                        await writer.WriteAsync(LpStudioService.TextFile(platform, output, project.Url).Replace("\n", "\r\n"));
                    }
                    foreach (var (assetId, i) in output.ImageAssetIds.Select((a, i) => (a, i + 1)))
                    {
                        await AddFileAsync($"{folder}/画像{i}.jpg", assetId);
                    }
                }
            }
            buffer.Position = 0;
            return Results.File(buffer, "application/zip", $"{Safe(project.Title)}.zip");
        });
    }

    private static string FileName(MediaAsset asset)
    {
        var ext = asset.Mime switch { "video/mp4" => ".mp4", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        var name = Path.GetFileNameWithoutExtension(asset.FileName);
        return $"{Safe(string.IsNullOrWhiteSpace(name) ? "reachforge" : name)}{ext}";
    }

    /// <summary>ファイル名に使えない文字を除く。</summary>
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "reachforge" : cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
