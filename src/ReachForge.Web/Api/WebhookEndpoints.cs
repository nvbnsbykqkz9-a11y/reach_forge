using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Social;
using ReachForge.Social.Webhooks;
using ReachForge.Infrastructure.Jobs;

namespace ReachForge.Web.Api;

/// <summary>
/// SNS の Webhook 受信（RF-DES-001 5.5）。署名（HMAC-SHA256）を検証してから受け付ける。
/// 検証後はキュー（WebhookProcessJob）へ登録してすぐ 200 を返し、取り込み（分類・統合受信箱・炎上検知）は後から行う。
/// キューへ登録できなければ 503 を返して SNS 側の再送に任せる。
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/v1/webhooks").AllowAnonymous().DisableAntiforgery();

        // Meta（Facebook / Instagram）・Threads の購読確認
        hooks.MapGet("/meta", (HttpRequest r, IOptions<SocialOptions> o) => Verify(r, o.Value.Meta.WebhookVerifyToken));
        hooks.MapGet("/threads", (HttpRequest r, IOptions<SocialOptions> o) => Verify(r, o.Value.Threads.WebhookVerifyToken));

        hooks.MapPost("/meta", async (HttpRequest r, IOptions<SocialOptions> o, IWorkQueue queue, ILogger<Program> log) =>
            await ReceiveAsync(r, o.Value.Meta.AppSecret, "meta", queue, log));
        hooks.MapPost("/threads", async (HttpRequest r, IOptions<SocialOptions> o, IWorkQueue queue, ILogger<Program> log) =>
            await ReceiveAsync(r, o.Value.Threads.AppSecret, "threads", queue, log));

        // LINE：チャネルごとの Webhook URL（チャネルシークレットで署名を検証）
        hooks.MapPost("/line/{channelId:guid}", async (Guid channelId, HttpRequest r, TenantContextOverride context,
            IServiceProvider services, IWorkQueue queue, ILogger<Program> log, CancellationToken ct) =>
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
            if (!await TryEnqueueAsync(queue, new WebhookWork("line", System.Text.Encoding.UTF8.GetString(body), channelId), log))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            log.LogInformation("LINE webhook accepted for channel {ChannelId} ({Bytes} bytes)", channelId, body.Length);
            return Results.Ok();
        });
    }

    private static IResult Verify(HttpRequest r, string? verifyToken) =>
        r.Query["hub.mode"] == "subscribe" && !string.IsNullOrEmpty(verifyToken) && r.Query["hub.verify_token"] == verifyToken
            ? Results.Text(r.Query["hub.challenge"].ToString())
            : Results.StatusCode(StatusCodes.Status403Forbidden);

    private static async Task<IResult> ReceiveAsync(HttpRequest r, string? appSecret, string source, IWorkQueue queue, ILogger log)
    {
        var body = await ReadBodyAsync(r);
        if (string.IsNullOrEmpty(appSecret) || !WebhookSignature.VerifyMeta(body, r.Headers["X-Hub-Signature-256"], appSecret))
        {
            return Results.Unauthorized();
        }
        // 重複配信は取り込み時に（チャネル＋SNS 上の ID）で排除する
        if (!await TryEnqueueAsync(queue, new WebhookWork(source, System.Text.Encoding.UTF8.GetString(body)), log))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        log.LogInformation("{Source} webhook accepted ({Bytes} bytes)", source, body.Length);
        return Results.Ok();
    }

    private static async Task<bool> TryEnqueueAsync(IWorkQueue queue, WebhookWork work, ILogger log)
    {
        try
        {
            await queue.EnqueueAsync(WorkQueues.Webhooks, work.ToJson(), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not enqueue {Source} webhook", work.Source);
            return false;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest r)
    {
        using var ms = new MemoryStream();
        await r.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
