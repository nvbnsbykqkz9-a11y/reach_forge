using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.YouTube;

/// <summary>
/// YouTube の連携（Google OAuth 2.0、PKCE）。access_type=offline と prompt=consent でリフレッシュトークンを必ず受け取り、
/// 1時間で失効するアクセストークンを使用前に更新する（ChannelTokenService）。チャンネル ID がアカウントの ID になる。
/// デスクトップ用の OAuth クライアントでは http://127.0.0.1 のリダイレクト先を使える。
/// </summary>
public sealed class YouTubeConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-youtube";
    private YouTubeOptions O => options.Value.YouTube;

    public bool Supports(SocialPlatform platform) => platform == SocialPlatform.YouTube && O.IsConfigured;
    public ConnectMode Mode => ConnectMode.OAuth;

    public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        $"{O.AuthorizeUrl}?" + SocialHttp.Query(
            ("client_id", O.ClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("scope", O.Scopes),
            ("access_type", "offline"), ("prompt", "consent"), ("include_granted_scopes", "true"), ("state", state),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"));

    public async Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code,
        string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var token = await TokenAsync(
        [
            new("code", code), new("client_id", O.ClientId!), new("client_secret", O.ClientSecret!),
            new("redirect_uri", redirectUri), new("grant_type", "authorization_code"), new("code_verifier", codeVerifier),
        ], ct);

        var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName),
            SocialHttp.Get("youtube/v3/channels?part=snippet&mine=true", token.AccessToken), "YouTube", ct);
        var channels = (json["items"] as JsonArray)?.OfType<JsonNode>().ToList() ?? [];
        if (channels.Count == 0)
        {
            throw new SocialApiException(ErrorCodes.PubFailed,
                "この Google アカウントには YouTube チャンネルがありません。YouTube でチャンネルを作成してから連携してください。", false);
        }
        return channels.Select(c => new ConnectedAccount(SocialPlatform.YouTube, SocialHttp.Str(c, "id"),
            SocialHttp.StrOrNull(c, "snippet.customUrl") ?? SocialHttp.StrOrNull(c, "snippet.title") ?? "YouTube",
            SocialHttp.StrOrNull(c, "snippet.thumbnails.default.url"), token, O.Scopes.Split(' '))).ToList();
    }

    public async Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, "YouTube の再認証が必要です（更新用トークンがありません）", false);
        }
        var refreshed = await TokenAsync(
        [
            new("client_id", O.ClientId!), new("client_secret", O.ClientSecret!),
            new("grant_type", "refresh_token"), new("refresh_token", token.RefreshToken),
        ], ct);
        // Google は更新時にリフレッシュトークンを返さない（同じものを使い続ける）
        return refreshed with { RefreshToken = refreshed.RefreshToken ?? token.RefreshToken, Extra = token.Extra };
    }

    private async Task<StoredToken> TokenAsync(KeyValuePair<string, string>[] fields, CancellationToken ct)
    {
        try
        {
            var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName),
                SocialHttp.Form(HttpMethod.Post, O.TokenUrl, fields), "YouTube", ct);
            return new StoredToken(SocialHttp.Str(json, "access_token"), SocialHttp.StrOrNull(json, "refresh_token"),
                SocialHttp.ExpiresAt(json, clock.GetUtcNow()));
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            // invalid_grant：利用者がアクセスを取り消した、またはテスト中のアプリのトークンが7日で失効した
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, $"YouTube の再認証が必要です（{ex.Message}）", false);
        }
    }
}
