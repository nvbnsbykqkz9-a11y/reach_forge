using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Infrastructure.Media;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Api;

/// <summary>
/// メディアの配信（/media/{token}）と API（/api/v1/media, /api/v1/ai/images, /api/v1/ai/jobs）。
/// 配信 URL は期限付きトークンで保護し、SNS（Instagram 等）が認証なしで取得できるようにする。
/// </summary>
public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{token}", async (string token, IDataProtectionProvider protection, TimeProvider clock,
            TenantContextOverride context, IServiceProvider services, IMediaStorage storage, HttpResponse response, CancellationToken ct) =>
        {
            if (AppMediaUrlSigner.Validate(protection, token, clock) is not { } id) return Results.NotFound();
            // テナントの絞り込みは DbContext 生成時に決まるため、上書きを設定してから取得する
            context.Current = new MutableTenantContext { IsSystem = true, UserName = "media" };
            var db = services.GetRequiredService<ReachForgeDbContext>();
            var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (asset is null) return Results.NotFound();
            response.Headers.CacheControl = "private, max-age=3600";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Stream(await storage.OpenReadAsync(asset.BlobPath, ct), asset.Mime, enableRangeProcessing: true);
        }).AllowAnonymous();

        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/media", async (MediaFilter? filter, MediaService media, CancellationToken ct) =>
        {
            var assets = await media.ListAsync(filter ?? MediaFilter.All, ct);
            var result = new List<object>();
            foreach (var a in assets)
            {
                result.Add(new { a.Id, a.FileName, a.Mime, a.Width, a.Height, a.Source, a.IsAiLabeled, a.AltText, url = await media.UrlAsync(a.Id, ct) });
            }
            return result;
        });
        api.MapPost("/media", async (IFormFile file, HttpRequest request, MediaService media, CancellationToken ct) =>
        {
            // 偽造防止：他サイトの HTML フォームは独自ヘッダーを付けられないため、API 呼び出しにはヘッダーを必須にする
            if (!request.Headers.ContainsKey("X-Requested-With"))
            {
                return Results.Problem(statusCode: 400, title: "E-SYS-400", detail: "X-Requested-With ヘッダーが必要です。");
            }
            await using var stream = file.OpenReadStream();
            var asset = await media.UploadAsync(file.FileName, file.ContentType, stream, file.Length, ct);
            return Results.Created($"/api/v1/media/{asset.Id}", new { asset.Id, asset.Width, asset.Height, asset.Mime });
        }).DisableAntiforgery(); // フォームの偽造防止トークンの代わりに X-Requested-With を必須にする

        api.MapPost("/ai/images", async (ImageJobRequest request, MediaService media, CancellationToken ct) =>
        {
            var job = await media.EnqueueGenerationAsync(request, ct);
            return Results.Accepted($"/api/v1/ai/jobs/{job.Id}", new { jobId = job.Id, job.CreditsHeld });
        });
        api.MapGet("/ai/jobs/{id:guid}", async (Guid id, MediaService media, CancellationToken ct) =>
        {
            var job = await media.GetJobAsync(id, ct);
            return new { job.Id, job.Status, job.Stage, job.ResultAssetIds, job.CreditsCharged, job.ErrorCode, job.Error };
        });
    }
}
