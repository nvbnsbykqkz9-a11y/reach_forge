using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Ads;

/// <summary>広告アカウントの候補（連携で取得したもの）。</summary>
public sealed record AdAccountInfo(string ExternalAccountId, string Name, string Currency, IReadOnlyDictionary<string, string>? Extra = null);

public sealed record AdConnectResult(StoredToken Token, IReadOnlyList<AdAccountInfo> Accounts);

/// <param name="StateSecret">連携の途中で覚えておく値（X の OAuth 1.0a の request token secret）。なければ PKCE の verifier を使う。</param>
/// <param name="StateKey">戻り先で連携を見分ける値が state 以外のとき（X は oauth_token で戻ってくる）。</param>
public sealed record AdAuthorizationStart(string Url, string? StateSecret = null, string? StateKey = null);

/// <summary>
/// 出稿に必要なもの。画像・動画はバイト列で各社にアップロードする（公開 URL は使わない）。
/// <paramref name="PageId"/>・<paramref name="InstagramUserId"/> は Meta、<paramref name="SocialUserId"/> は X の投稿者。
/// </summary>
public sealed record AdSubmission(
    AdCampaign Campaign,
    AdAccount Account,
    StoredToken Token,
    PublishMedia? Media,
    byte[]? Thumbnail,
    string? PageId,
    string? InstagramUserId,
    string? SocialUserId,
    string BrandName);

public sealed record AdSubmitResult(IReadOnlyDictionary<string, string> ExternalIds, AdStatus Status,
    IReadOnlyDictionary<string, string>? AccountExtra = null);

public sealed record AdRemoteState(AdStatus Status, AdResults? Results, string? ReviewNote);

/// <summary>
/// 広告マネージャーとの連携（各社ごとに実装）。出稿は すべて停止した状態でつくる → 全部できたら配信を始める、の順で行い、
/// 途中で失敗したらつくった分を消してから例外（SocialApiException）を投げる。
/// </summary>
public interface IAdNetworkAdapter
{
    AdNetwork Network { get; }

    /// <summary>お試し（実際には出稿しない）か。</summary>
    bool IsSimulation { get; }

    /// <summary>アプリの設定（ID・シークレット・開発者トークン）がそろっているか。</summary>
    bool IsConfigured { get; }

    /// <summary>この仕組みで広告を出せる SNS。</summary>
    IReadOnlyList<SocialPlatform> Platforms { get; }

    /// <summary>1日の予算の下限（各社の最低額）。</summary>
    decimal MinDailyBudget(string currency);

    /// <summary>動画が必要か（TikTok・YouTube）。</summary>
    bool RequiresVideo(SocialPlatform platform);

    Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct);

    /// <param name="stateSecret">BeginAuthorizationAsync の StateSecret、なければ PKCE の verifier。</param>
    Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct);

    /// <summary>トークンを更新する（更新できなければ RequiresReauth の SocialApiException）。</summary>
    Task<StoredToken> RefreshAsync(StoredToken token, CancellationToken ct) => Task.FromResult(token);

    Task<AdSubmitResult> SubmitAsync(AdSubmission submission, CancellationToken ct);

    Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct);

    Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct);
}

public interface IAdNetworkFactory
{
    /// <summary>広告の仕組み。設定がなく、お試しも無効なら null。</summary>
    IAdNetworkAdapter? Get(AdNetwork network, bool demo);
}

/// <summary>SNS と広告の仕組みの対応。</summary>
public static class AdNetworks
{
    /// <summary>この SNS の広告をどこから出すか。広告 API が一般に公開されていない SNS（LINE・Threads）は null。</summary>
    public static AdNetwork? For(SocialPlatform platform) => platform switch
    {
        SocialPlatform.Facebook or SocialPlatform.Instagram => AdNetwork.Meta,
        SocialPlatform.TikTok => AdNetwork.TikTok,
        SocialPlatform.X => AdNetwork.X,
        SocialPlatform.YouTube => AdNetwork.Google,
        _ => null,
    };

    public static string DisplayName(AdNetwork n) => n switch
    {
        AdNetwork.Meta => "Meta 広告マネージャ",
        AdNetwork.TikTok => "TikTok 広告マネージャー",
        AdNetwork.X => "X 広告",
        AdNetwork.Google => "Google 広告",
        _ => "お試しの広告アカウント",
    };

    /// <summary>広告アカウントと支払い方法を用意する場所（初心者への案内）。</summary>
    public static string ManagerUrl(AdNetwork n) => n switch
    {
        AdNetwork.Meta => "https://adsmanager.facebook.com/",
        AdNetwork.TikTok => "https://ads.tiktok.com/",
        AdNetwork.X => "https://ads.x.com/",
        AdNetwork.Google => "https://ads.google.com/",
        _ => "",
    };

    /// <summary>API で広告を出せない SNS の公式の広告の案内。</summary>
    public static string? ManualGuideUrl(SocialPlatform p) => p switch
    {
        SocialPlatform.Line => "https://www.lycbiz.com/jp/service/line-ads/",
        SocialPlatform.Threads => "https://www.facebook.com/business/ads",
        _ => null,
    };

    /// <summary>連携のコールバックの URL のパス（各社の開発者サイトに登録する）。</summary>
    public static string CallbackPath(AdNetwork n) => $"api/v1/oauth/ads/{n.ToString().ToLowerInvariant()}/callback";
}
