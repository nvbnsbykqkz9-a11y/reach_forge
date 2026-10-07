using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Media;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Api;

/// <summary>
/// メディアの配信（/media/{token}）。画面の画像・動画の表示に使う。URL は期限付きトークンで保護する。
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
    }
}
