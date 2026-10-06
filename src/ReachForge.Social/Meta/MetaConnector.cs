using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Meta;

/// <summary>
/// Facebook ページ・Instagram（ビジネス／クリエイター）の連携（Facebook Login for Business、F-01-3）。
/// 認可コード → 短期ユーザートークン → 長期ユーザートークン（約60日）に交換し、管理ページの一覧（ページトークン付き）を取得する。
/// 長期ユーザートークンから得たページトークンは失効しないため、ページ・IG の投稿にはページトークンを使う。
/// </summary>
public sealed class MetaConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-meta";
    private MetaOptions O => options.Value.Meta;

    public bool Supports(SocialPlatform platform) =>
        platform is SocialPlatform.Facebook or SocialPlatform.Instagram && O.IsConfigured;

    public ConnectMode Mode => ConnectMode.OAuth;

    public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        $"{O.DialogBaseUrl}{O.GraphVersion}/dialog/oauth?" + SocialHttp.Query(
            ("client_id", O.AppId), ("redirect_uri", redirectUri), ("state", state), ("response_type", "code"),
            ("scope", platform == SocialPlatform.Instagram ? O.InstagramScopes : O.FacebookScopes));

    public async Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code,
        string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        var shortLived = await SocialHttp.SendAsync(client, SocialHttp.Get($"{O.GraphVersion}/oauth/access_token?" + SocialHttp.Query(
            ("client_id", O.AppId), ("client_secret", O.AppSecret), ("redirect_uri", redirectUri), ("code", code))), "Meta", ct);

        var longLived = await SocialHttp.SendAsync(client, SocialHttp.Get($"{O.GraphVersion}/oauth/access_token?" + SocialHttp.Query(
            ("grant_type", "fb_exchange_token"), ("client_id", O.AppId), ("client_secret", O.AppSecret),
            ("fb_exchange_token", SocialHttp.Str(shortLived, "access_token")))), "Meta", ct);
        var userToken = SocialHttp.Str(longLived, "access_token");

        var pages = await SocialHttp.SendAsync(client, SocialHttp.Get(
            $"{O.GraphVersion}/me/accounts?fields=id,name,access_token,picture%7Burl%7D," +
            "instagram_business_account%7Bid,username,profile_picture_url%7D&limit=100", userToken), "Meta", ct);

        var scopes = (platform == SocialPlatform.Instagram ? O.InstagramScopes : O.FacebookScopes).Split(',');
        var accounts = new List<ConnectedAccount>();
        foreach (var page in pages["data"]?.AsArray() ?? [])
        {
            var pageId = SocialHttp.Str(page, "id");
            var pageToken = new StoredToken(SocialHttp.Str(page, "access_token")).With("pageId", pageId);
            accounts.Add(new ConnectedAccount(SocialPlatform.Facebook, pageId, SocialHttp.Str(page, "name"),
                SocialHttp.StrOrNull(page, "picture.data.url"), pageToken, scopes));

            if (page?["instagram_business_account"] is JsonObject ig)
            {
                accounts.Add(new ConnectedAccount(SocialPlatform.Instagram, SocialHttp.Str(ig, "id"),
                    "@" + SocialHttp.Str(ig, "username"), SocialHttp.StrOrNull(ig, "profile_picture_url"), pageToken, scopes));
            }
        }
        _ = clock; // ページトークンは失効しないため期限は持たない
        return accounts;
    }

    /// <summary>ページトークンは失効しない。取り消された場合は投稿時に 190 エラーとなり、要再接続になる。</summary>
    public Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct) =>
        token.ExpiresAt is null
            ? Task.FromResult(token)
            : throw new SocialApiException(ErrorCodes.SnsReauthRequired, "Meta の再認証が必要です", isTransient: false);
}
