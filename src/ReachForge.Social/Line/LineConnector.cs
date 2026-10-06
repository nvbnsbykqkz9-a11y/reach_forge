using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Line;

/// <summary>
/// LINE 公式アカウント（Messaging API）の連携。OAuth ではなく、LINE Developers のチャネル ID・チャネルシークレットを入力してもらい、
/// ステートレスチャネルアクセストークン（v3、約15分）をクライアント認証で都度発行する。
/// チャネルシークレットは Webhook の署名検証にも使うため、暗号化した資格情報ストアに保存する。
/// </summary>
public sealed class LineConnector(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : IChannelConnector
{
    public const string HttpClientName = "sns-line";

    public bool Supports(SocialPlatform platform) => platform == SocialPlatform.Line && options.Value.Line.Enabled;
    public ConnectMode Mode => ConnectMode.Credentials;

    public IReadOnlyList<CredentialField> CredentialFields =>
    [
        new("channelId", "チャネルID", Secret: false, Required: true, "LINE Developers コンソール ＞ Messaging API チャネル ＞ チャネル基本設定"),
        new("channelSecret", "チャネルシークレット", Secret: true, Required: true, "同じ画面の「チャネルシークレット」。Webhook の署名検証にも使います"),
    ];

    public async Task<IReadOnlyList<ConnectedAccount>> ConnectAsync(SocialPlatform platform,
        IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        var channelId = fields["channelId"].Trim();
        var secret = fields["channelSecret"].Trim();
        StoredToken token;
        try
        {
            token = await IssueAsync(channelId, secret, ct);
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            throw new DomainException(ErrorCodes.SnsAuthCanceled,
                "LINE のチャネルIDまたはチャネルシークレットが正しくありません。LINE Developers コンソールの値を確認してください。");
        }

        var bot = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), SocialHttp.Get("v2/bot/info", token.AccessToken), "LINE", ct);
        return
        [
            new ConnectedAccount(SocialPlatform.Line, SocialHttp.Str(bot, "userId"),
                SocialHttp.StrOrNull(bot, "displayName") ?? SocialHttp.Str(bot, "basicId"), SocialHttp.StrOrNull(bot, "pictureUrl"),
                token, ["messaging_api"]),
        ];
    }

    public async Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
    {
        var channelId = token.Get("channelId");
        var secret = token.Get("channelSecret");
        if (channelId is null || secret is null)
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, "LINE の再設定が必要です", isTransient: false);
        }
        try
        {
            return await IssueAsync(channelId, secret, ct);
        }
        catch (SocialApiException ex) when (!ex.IsTransient)
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, $"LINE の再設定が必要です（{ex.Message}）", false);
        }
    }

    /// <summary>ステートレスチャネルアクセストークンを発行する（POST /oauth2/v3/token）。</summary>
    private async Task<StoredToken> IssueAsync(string channelId, string secret, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), SocialHttp.Form(HttpMethod.Post, "oauth2/v3/token",
        [
            new("grant_type", "client_credentials"), new("client_id", channelId), new("client_secret", secret),
        ]), "LINE", ct);
        return new StoredToken(SocialHttp.Str(json, "access_token"), null, SocialHttp.ExpiresAt(json, clock.GetUtcNow()),
            new Dictionary<string, string> { ["channelId"] = channelId, ["channelSecret"] = secret });
    }
}
