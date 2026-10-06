using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Social;
using ReachForge.Social.Webhooks;

namespace ReachForge.Web.Api;

/// <summary>
/// SNS の Webhook 受信（RF-DES-001 5.5）。署名（HMAC-SHA256）を検証してから受け付ける。
/// 取り込み（分類・統合受信箱・炎上検知）は F-09 で Service Bus 経由の WebhookProcessJob として実装する。
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/v1/webhooks").AllowAnonymous().DisableAntiforgery();

        // Meta（Facebook / Instagram）・Threads の購読確認
        hooks.MapGet("/meta", (HttpRequest r, IOptions<SocialOptions> o) => Verify(r, o.Value.Meta.WebhookVerifyToken));
        hooks.MapGet("/threads", (HttpRequest r, IOptions<SocialOptions> o) => Verify(r, o.Value.Threads.WebhookVerifyToken));

        hooks.MapPost("/meta", async (HttpRequest r, IOptions<SocialOptions> o, ILogger<Program> log) =>
            await ReceiveAsync(r, o.Value.Meta.AppSecret, "meta", log));
        hooks.MapPost("/threads", async (HttpRequest r, IOptions<SocialOptions> o, ILogger<Program> log) =>
            await ReceiveAsync(r, o.Value.Threads.AppSecret, "threads", log));

        // LINE：チャネルごとの Webhook URL（チャネルシークレットで署名を検証）
        hooks.MapPost("/line/{channelId:guid}", async (Guid channelId, HttpRequest r, TenantContextOverride context,
            IServiceProvider services, ILogger<Program> log, CancellationToken ct) =>
        {
            context.Current = new MutableTenantContext { IsSystem = true, UserName = "webhook" };
            var db = services.GetRequiredService<ReachForgeDbContext>();
            var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.Platform == SocialPlatform.Line, ct);
            if (channel is null) return Results.NotFound();
            var token = await services.GetRequiredService<ICredentialStore>().LoadAsync(channel.CredentialSecretRef, ct);
            var body = await ReadBodyAsync(r);
            if (token?.Get("channelSecret") is not { } secret || !WebhookSignature.VerifyLine(body, r.Headers["X-Line-Signature"], secret))
            {
                return Results.Unauthorized();
            }
            log.LogInformation("LINE webhook accepted for channel {ChannelId} ({Bytes} bytes)", channelId, body.Length);
            return Results.Ok();
        });
    }

    private static IResult Verify(HttpRequest r, string? verifyToken) =>
        r.Query["hub.mode"] == "subscribe" && !string.IsNullOrEmpty(verifyToken) && r.Query["hub.verify_token"] == verifyToken
            ? Results.Text(r.Query["hub.challenge"].ToString())
            : Results.StatusCode(StatusCodes.Status403Forbidden);

    private static async Task<IResult> ReceiveAsync(HttpRequest r, string? appSecret, string source, ILogger log)
    {
        var body = await ReadBodyAsync(r);
        if (string.IsNullOrEmpty(appSecret) || !WebhookSignature.VerifyMeta(body, r.Headers["X-Hub-Signature-256"], appSecret))
        {
            return Results.Unauthorized();
        }
        // TODO(F-09): 冪等キー（SNS＋イベントID）で重複排除し、Service Bus へ投入する
        log.LogInformation("{Source} webhook accepted ({Bytes} bytes)", source, body.Length);
        return Results.Ok();
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest r)
    {
        using var ms = new MemoryStream();
        await r.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
