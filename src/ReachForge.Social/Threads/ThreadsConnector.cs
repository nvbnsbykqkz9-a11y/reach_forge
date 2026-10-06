using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Threads;

/// <summary>
/// Threads の連携。認可コード → 短期トークン（1時間）→ 長期トークン（60日、th_exchange_token）。
/// 長期トークンは発行から24時間以上経過していれば th_refresh_token で60日延長できる。
/// </summary>
public sealed class ThreadsConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-threads";
    private ThreadsOptions O => options.Value.Threads;

    public bool Supports(SocialPlatform platform) => platform == SocialPlatform.Threads && O.IsConfigured;
    public ConnectMode Mode => ConnectMode.OAuth;

    public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        $"{O.AuthorizeUrl}?" + SocialHttp.Query(
            ("client_id", O.AppId), ("redirect_uri", redirectUri), ("scope", O.Scopes), ("response_type", "code"),
            ("state", state));

    public async Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code,
        string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        var shortLived = await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post, "oauth/access_token",
        [
            new("client_id", O.AppId!), new("client_secret", O.AppSecret!), new("grant_type", "authorization_code"),
            new("redirect_uri", redirectUri), new("code", code),
        ]), "Threads", ct);

        var longLived = await SocialHttp.SendAsync(client, SocialHttp.Get("access_token?" + SocialHttp.Query(
            ("grant_type", "th_exchange_token"), ("client_secret", O.AppSecret),
            ("access_token", SocialHttp.Str(shortLived, "access_token")))), "Threads", ct);
        var token = new StoredToken(SocialHttp.Str(longLived, "access_token"), null, SocialHttp.ExpiresAt(longLived, clock.GetUtcNow()));

        var me = await SocialHttp.SendAsync(client,
            SocialHttp.Get($"{O.ApiVersion}/me?fields=id,username,threads_profile_picture_url", token.AccessToken), "Threads", ct);
        var username = SocialHttp.Str(me, "username");
        return
        [
            new ConnectedAccount(SocialPlatform.Threads, SocialHttp.Str(me, "id"), "@" + username,
                SocialHttp.StrOrNull(me, "threads_profile_picture_url"), token.With("username", username), O.Scopes.Split(',')),
        ];
    }

    public async Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
    {
        try
        {
            var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), SocialHttp.Get("refresh_access_token?" +
                SocialHttp.Query(("grant_type", "th_refresh_token"), ("access_token", token.AccessToken))), "Threads", ct);
            return token with
            {
                AccessToken = SocialHttp.Str(json, "access_token"),
                ExpiresAt = SocialHttp.ExpiresAt(json, clock.GetUtcNow()),
            };
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, $"Threads の再認証が必要です（{ex.Message}）", false);
        }
    }
}
