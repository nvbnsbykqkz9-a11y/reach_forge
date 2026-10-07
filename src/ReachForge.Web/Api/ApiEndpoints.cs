using Microsoft.AspNetCore.Mvc;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Web.Api;

/// <summary>
/// SNS・広告アカウントの連携の戻り先（OAuth コールバック）。外部連携用の REST API と API キーは廃止し、画面からだけ操作する。
/// </summary>
public static class ApiEndpoints
{
    public static void MapReachForgeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("ReachForge").RequireAuthorization();

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
    }
}
