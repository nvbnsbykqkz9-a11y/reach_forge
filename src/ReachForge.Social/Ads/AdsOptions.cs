namespace ReachForge.Social.Ads;

/// <summary>
/// 有料広告（各社の広告 API）の設定。Meta は SNS 連携と同じアプリ（Social:Meta）を使い、広告の権限（ads_management）を追加で求める。
/// API のバージョン・URL は各社の変更に追随できるよう設定値にする。
/// </summary>
public sealed class AdsOptions
{
    public const string SectionName = "Ads";

    public MetaAdsOptions Meta { get; set; } = new();
    public TikTokAdsOptions TikTok { get; set; } = new();
    public XAdsOptions X { get; set; } = new();
    public GoogleAdsOptions Google { get; set; } = new();
}

public sealed class MetaAdsOptions
{
    public string Scopes { get; set; } =
        "ads_management,ads_read,business_management,pages_show_list,pages_read_engagement,instagram_basic";
}

/// <summary>TikTok API for Business（Marketing API）。TikTok for Developers（投稿用）とは別のアプリ。</summary>
public sealed class TikTokAdsOptions
{
    public string? AppId { get; set; }
    public string? Secret { get; set; }
    public string AuthorizeUrl { get; set; } = "https://business-api.tiktok.com/portal/auth";
    public string ApiBaseUrl { get; set; } = "https://business-api.tiktok.com/open_api/v1.3/";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(Secret);
}

/// <summary>X Ads API（OAuth 1.0a。Ads API の利用申請が承認されたアプリの API Key・Secret）。</summary>
public sealed class XAdsOptions
{
    public string? ConsumerKey { get; set; }
    public string? ConsumerSecret { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.x.com/";
    public string AdsApiBaseUrl { get; set; } = "https://ads-api.x.com/12/";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConsumerKey) && !string.IsNullOrWhiteSpace(ConsumerSecret);
}

/// <summary>Google Ads API（YouTube の広告。デマンド ジェネレーション キャンペーン）。</summary>
public sealed class GoogleAdsOptions
{
    /// <summary>Google 広告の API センターで発行する開発者トークン。</summary>
    public string? DeveloperToken { get; set; }

    /// <summary>MCC（クライアント センター）経由で操作するときの MCC のお客様 ID（数字だけ）。</summary>
    public string? LoginCustomerId { get; set; }

    /// <summary>OAuth クライアント。空なら YouTube の連携（Social:YouTube）のものを使う。</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string ApiVersion { get; set; } = "v21";
    public string ApiBaseUrl { get; set; } = "https://googleads.googleapis.com/";
    public string AuthorizeUrl { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenUrl { get; set; } = "https://oauth2.googleapis.com/token";
    public string Scopes { get; set; } = "https://www.googleapis.com/auth/adwords";
}
