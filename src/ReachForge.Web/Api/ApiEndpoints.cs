using Microsoft.AspNetCore.Mvc;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Web.Api;

/// <summary>
/// 外部連携用 REST API（RF-DES-001 13章）。ベース URL は /api/v1、日時は ISO 8601（UTC）。
/// 認証（Bearer / API キー）・レート制限・Idempotency-Key・カーソルページングは後続で追加する。
/// </summary>
public static class ApiEndpoints
{
    public sealed record ConnectResponse(string Mode, string? AuthorizationUrl, Guid? ChannelId, string[] CredentialFields);

    public static string CallbackUrl(HttpContext http, SocialPlatform platform) =>
        $"{http.Request.Scheme}://{http.Request.Host}/api/v1/oauth/callback/{platform}";
    public sealed record SavePostRequest(string Title, PostObjective Objective, string CoreMessage, string? Cta,
        string[]? Hashtags, Guid[]? ProductIds, Guid? CampaignId, Guid? AiGenerationId, string? Theme);
    public sealed record GenerateVariantsRequest(Guid[] ChannelIds, string? LinkUrl, bool IncludeUrlForX);
    public sealed record PatchVariantRequest(string Body, string[]? Hashtags, string? Title, bool? UrlCostAcknowledged);
    public sealed record SubmitRequest(DateTimeOffset? RequestedPublishAt, string? Comment);
    public sealed record ApproveRequest(string? Comment, string? ExceptionReason);
    public sealed record RejectRequest(string Reason);
    public sealed record ScheduleRequest(DateTimeOffset? ScheduledAt);

    public static void MapReachForgeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("ReachForge").RequireAuthorization();

        // F-01 チャネル連携
        api.MapGet("/channels", (ChannelService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("/channels/{platform}/connect", async (SocialPlatform platform, HttpContext http, ChannelService s,
            CancellationToken ct) =>
        {
            var start = await s.BeginConnectAsync(platform, CallbackUrl(http, platform), ct);
            return new ConnectResponse(start.Mode.ToString(), start.AuthorizationUrl, start.Connected?.Id,
                start.Fields.Select(f => f.Key).ToArray());
        });
        api.MapPost("/channels/{platform}/credentials", async (SocialPlatform platform, Dictionary<string, string> fields,
            ChannelService s, CancellationToken ct) =>
        {
            var outcome = await s.ConnectWithCredentialsAsync(platform, fields, ct);
            return new { channelId = outcome.Channel?.Id, selectionKey = outcome.SelectionKey };
        });
        // OAuth コールバック（各 SNS アプリに「https://<ホスト>/api/v1/oauth/callback/<SNS>」を登録する）
        api.MapGet("/oauth/callback/{platform}", async (SocialPlatform platform, string? code, string? state, string? error,
            ChannelService s, CancellationToken ct) =>
        {
            try
            {
                var outcome = await s.CompleteOAuthAsync(platform, code, state, error, ct);
                return outcome.SelectionKey is { } key
                    ? Results.LocalRedirect($"/settings/channels/select/{Uri.EscapeDataString(key)}")
                    : Results.LocalRedirect($"/settings/channels?connected={platform}");
            }
            catch (ReachForge.Domain.Common.DomainException ex)
            {
                return Results.LocalRedirect($"/settings/channels?error={Uri.EscapeDataString(ex.Message)}");
            }
        });
        // 広告アカウントの連携の戻り先（各社の広告アプリに「https://<ホスト>/api/v1/oauth/ads/<meta|tiktok|x|google>/callback」を登録する）
        // TikTok は auth_code、X（OAuth 1.0a）は oauth_token と oauth_verifier で返ってくる
        api.MapGet("/oauth/ads/{network}/callback", async (string network, string? code, [FromQuery(Name = "auth_code")] string? authCode,
            [FromQuery(Name = "oauth_token")] string? oauthToken, [FromQuery(Name = "oauth_verifier")] string? oauthVerifier,
            string? state, string? error, string? denied, AdService s, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AdNetwork>(network, ignoreCase: true, out var n) || n == AdNetwork.Demo) return Results.NotFound();
            var fallback = n switch
            {
                AdNetwork.TikTok => SocialPlatform.TikTok,
                AdNetwork.X => SocialPlatform.X,
                AdNetwork.Google => SocialPlatform.YouTube,
                _ => SocialPlatform.Instagram,
            };
            try
            {
                var effectiveCode = code ?? authCode ?? (oauthToken is not null && oauthVerifier is not null ? $"{oauthToken}:{oauthVerifier}" : null);
                var key = state ?? (n == AdNetwork.X && (oauthToken ?? denied) is { } t ? "ad_x_" + t : null);
                var platform = await s.CompleteConnectAsync(n, effectiveCode, key, error ?? (denied is null ? null : "denied"), ct);
                return Results.LocalRedirect($"/create/{platform.ToString().ToLowerInvariant()}?tab=ad&adConnected=1");
            }
            catch (Exception ex) when (ex is ReachForge.Domain.Common.DomainException or ReachForge.Application.Social.SocialApiException)
            {
                return Results.LocalRedirect($"/create/{fallback.ToString().ToLowerInvariant()}?tab=ad&error={Uri.EscapeDataString(ex.Message)}");
            }
        });
        api.MapDelete("/channels/{id:guid}", async (Guid id, ChannelService s, CancellationToken ct) =>
            Results.Ok(new { heldPosts = await s.DisconnectAsync(id, ct) }));
        api.MapGet("/channels/{id:guid}/best-times", (Guid id, SchedulingService s, CancellationToken ct) =>
            s.BestTimesAsync(id, ct));

        // F-02 ブランドプロファイル
        api.MapGet("/brand-profile", (WorkspaceService s, CancellationToken ct) => s.GetBrandAsync(ct));
        api.MapPut("/brand-profile", (BrandProfile body, WorkspaceService s, CancellationToken ct) => s.SaveBrandAsync(body, ct));

        // F-03 投稿文生成
        api.MapPost("/ai/copies", (CopyRequest body, StudioService s, CancellationToken ct) => s.GenerateCopiesAsync(body, ct))
            .RequireRateLimiting(Hosting.ApiProtection.AiPolicy);

        // F-06 マスター投稿・バリアント
        api.MapPost("/posts", (SavePostRequest r, StudioService s, CancellationToken ct) => s.SaveMasterPostAsync(new SaveMasterPost
        {
            Title = r.Title, Objective = r.Objective, CoreMessage = r.CoreMessage, Cta = r.Cta ?? "", Theme = r.Theme ?? "",
            Hashtags = r.Hashtags ?? [], ProductIds = r.ProductIds ?? [], CampaignId = r.CampaignId, AiGenerationId = r.AiGenerationId,
        }, ct));
        api.MapPost("/posts/{id:guid}/variants:generate", (Guid id, GenerateVariantsRequest r, StudioService s,
            CancellationToken ct) => s.GenerateVariantsAsync(id, r.ChannelIds, new VariantOptions(r.LinkUrl, r.IncludeUrlForX), ct))
            .RequireRateLimiting(Hosting.ApiProtection.AiPolicy);
        api.MapGet("/posts/{id:guid}/variants", (Guid id, StudioService s, CancellationToken ct) => s.GetVariantsAsync(id, ct));
        api.MapPatch("/variants/{id:guid}", async (Guid id, PatchVariantRequest r, StudioService s, CancellationToken ct) =>
        {
            var result = await s.UpdateVariantAsync(id, r.Body, r.Hashtags ?? [], r.Title, r.UrlCostAcknowledged, ct);
            return new { variant = result.Variant, reapprovalRequired = result.ReapprovalRequired };
        });

        // F-07 承認
        api.MapGet("/approvals", async (ApprovalService s, CancellationToken ct) =>
            (await s.QueueAsync(ct)).Select(i => new { i.Variant, i.Post.Title, i.Channel.DisplayName, i.RequestedBy }));
        api.MapPost("/variants/{id:guid}:submit", async (Guid id, SubmitRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.SubmitAsync([id], r.RequestedPublishAt, r.Comment, ct);
            return Results.NoContent();
        });
        api.MapPost("/variants/{id:guid}:approve", async (Guid id, ApproveRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.ApproveAsync(id, r.Comment, r.ExceptionReason, ct);
            return Results.NoContent();
        });
        api.MapPost("/variants/{id:guid}:reject", async (Guid id, RejectRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.RejectAsync(id, r.Reason, ct);
            return Results.NoContent();
        });

        // F-08 予約（scheduledAt 省略時は最適時刻の第1候補）
        api.MapPost("/variants/{id:guid}:schedule", (Guid id, ScheduleRequest r, SchedulingService s, CancellationToken ct) =>
            r.ScheduledAt is { } at ? s.ScheduleAsync(id, at, ct) : s.ScheduleAtBestTimeAsync(id, ct));
        api.MapGet("/calendar", (DateTimeOffset from, DateTimeOffset to, SchedulingService s, CancellationToken ct) =>
            s.CalendarAsync(from, to, ct));

        // F-10 / F-13
        api.MapGet("/dashboard", (DashboardService s, CancellationToken ct) => s.GetAsync(ct));
        api.MapGet("/billing/usage", async (WorkspaceService s, CancellationToken ct) =>
        {
            var u = await s.UsageAsync(ct);
            return new
            {
                u.Account.MonthlyGrant, u.Account.Balance, u.Account.Held, u.Account.Available, u.Account.IsLow,
                u.ByFeature, u.XPostsThisPeriod, u.EstimatedSnsCostUsd, u.ProjectedExhaustion,
            };
        });
    }
}
