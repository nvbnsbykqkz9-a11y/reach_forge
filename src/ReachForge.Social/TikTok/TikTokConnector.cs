using System.Buffers.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.TikTok;

/// <summary>
/// TikTok の連携（Login Kit v2）。アクセストークンは24時間、リフレッシュトークンは365日有効で、
/// 使用前に更新する（ChannelTokenService）。open_id がアカウントの ID になる。
/// </summary>
public sealed class TikTokConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-tiktok";
    private TikTokOptions O => options.Value.TikTok;

    public bool Supports(SocialPlatform platform) => platform == SocialPlatform.TikTok && O.IsConfigured;
    public ConnectMode Mode => ConnectMode.OAuth;

    public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        $"{O.AuthorizeUrl}?" + SocialHttp.Query(
            ("client_key", O.ClientKey), ("response_type", "code"), ("scope", O.Scopes), ("redirect_uri", redirectUri),
            ("state", state),
            // デスクトップアプリの PKCE は SHA-256 を16進で表す（RFC 7636 の Base64URL ではない）
            ("code_challenge", O.Desktop ? HexChallenge(codeChallenge) : null),
            ("code_challenge_method", O.Desktop ? "S256" : null));

    /// <summary>Base64URL の S256 チャレンジを、TikTok のデスクトップアプリが求める16進表記に直す（ハッシュ値は同じ）。</summary>
    public static string HexChallenge(string base64UrlChallenge) =>
        Convert.ToHexStringLower(Base64Url.DecodeFromChars(base64UrlChallenge));

    public async Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code,
        string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("client_key", O.ClientKey!), new("client_secret", O.ClientSecret!), new("code", code),
            new("grant_type", "authorization_code"), new("redirect_uri", redirectUri),
        };
        if (O.Desktop) fields.Add(new("code_verifier", codeVerifier));
        var token = await TokenAsync(fields, ct);

        var me = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), SocialHttp.Get(
            "v2/user/info/?fields=open_id,avatar_url,display_name,username", token.AccessToken), "TikTok", ct);
        var user = me["data"]?["user"];
        var username = SocialHttp.StrOrNull(user, "username");
        var name = username is null ? SocialHttp.StrOrNull(user, "display_name") ?? "TikTok" : "@" + username;
        return
        [
            new ConnectedAccount(SocialPlatform.TikTok, token.Get("open_id") ?? SocialHttp.Str(user, "open_id"), name,
                SocialHttp.StrOrNull(user, "avatar_url"), username is null ? token : token.With("username", username),
                O.Scopes.Split(',')),
        ];
    }

    public async Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, "TikTok の再認証が必要です（更新用トークンがありません）", false);
        }
        var refreshed = await TokenAsync(
        [
            new("client_key", O.ClientKey!), new("client_secret", O.ClientSecret!),
            new("grant_type", "refresh_token"), new("refresh_token", token.RefreshToken),
        ], ct);
        return refreshed with { Extra = token.Extra };
    }

    private async Task<StoredToken> TokenAsync(List<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        JsonNode json;
        try
        {
            json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName),
                SocialHttp.Form(HttpMethod.Post, "v2/oauth/token/", fields), "TikTok", ct);
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, $"TikTok の再認証が必要です（{ex.Message}）", false);
        }
        // TikTok はトークンのエラーを 200 で返すことがある（error / error_description）
        if (SocialHttp.StrOrNull(json, "access_token") is not { } accessToken)
        {
            var detail = SocialHttp.StrOrNull(json, "error_description") ?? SocialHttp.StrOrNull(json, "error") ?? "トークンを取得できませんでした";
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, $"TikTok の再認証が必要です（{detail}）", false);
        }
        var token = new StoredToken(accessToken, SocialHttp.StrOrNull(json, "refresh_token"), SocialHttp.ExpiresAt(json, clock.GetUtcNow()));
        return SocialHttp.StrOrNull(json, "open_id") is { } openId ? token.With("open_id", openId) : token;
    }
}
