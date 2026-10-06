using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.X;

/// <summary>
/// X の連携（OAuth 2.0 Authorization Code Flow with PKCE）。アクセストークンは約2時間で失効するため、
/// offline.access で取得したリフレッシュトークンで使用前に更新する（ChannelTokenService）。
/// </summary>
public sealed class XConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-x";
    private XOptions O => options.Value.X;

    public bool Supports(SocialPlatform platform) => platform == SocialPlatform.X && O.IsConfigured;
    public ConnectMode Mode => ConnectMode.OAuth;

    public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        $"{O.AuthorizeUrl}?" + SocialHttp.Query(
            ("response_type", "code"), ("client_id", O.ClientId), ("redirect_uri", redirectUri), ("scope", O.Scopes),
            ("state", state), ("code_challenge", codeChallenge), ("code_challenge_method", "S256"));

    public async Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code,
        string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var token = await TokenAsync(
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri),
            new("code_verifier", codeVerifier), new("client_id", O.ClientId!),
        ], ct);

        var client = http.CreateClient(HttpClientName);
        var me = await SocialHttp.SendAsync(client,
            SocialHttp.Get("2/users/me?user.fields=profile_image_url", token.AccessToken), "X", ct);
        var username = SocialHttp.Str(me, "data.username");
        return
        [
            new ConnectedAccount(SocialPlatform.X, SocialHttp.Str(me, "data.id"), "@" + username,
                SocialHttp.StrOrNull(me, "data.profile_image_url"), token.With("username", username), O.Scopes.Split(' ')),
        ];
    }

    public async Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new SocialApiException(Domain.Common.ErrorCodes.SnsReauthRequired, "X の再認証が必要です（更新用トークンがありません）", false);
        }
        var refreshed = await TokenAsync(
        [
            new("grant_type", "refresh_token"), new("refresh_token", token.RefreshToken), new("client_id", O.ClientId!),
        ], ct);
        return refreshed with { Extra = token.Extra };
    }

    private async Task<StoredToken> TokenAsync(KeyValuePair<string, string>[] fields, CancellationToken ct)
    {
        var request = SocialHttp.Form(HttpMethod.Post, "2/oauth2/token", fields);
        if (!string.IsNullOrEmpty(O.ClientSecret))
        {
            // 機密クライアントは Basic 認証（client_id:client_secret）
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{O.ClientId}:{O.ClientSecret}")));
        }
        try
        {
            var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), request, "X", ct);
            return new StoredToken(SocialHttp.Str(json, "access_token"), SocialHttp.StrOrNull(json, "refresh_token"),
                SocialHttp.ExpiresAt(json, clock.GetUtcNow()));
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            // invalid_grant など：リフレッシュトークンの失効・取り消し
            throw new SocialApiException(Domain.Common.ErrorCodes.SnsReauthRequired, $"X の再認証が必要です（{ex.Message}）", false);
        }
    }
}
