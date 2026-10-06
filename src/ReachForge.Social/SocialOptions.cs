namespace ReachForge.Social;

/// <summary>
/// SNS 連携の設定。App ID・シークレットは Key Vault / User Secrets / 環境変数で渡し、リポジトリに置かない。
/// API バージョン・URL は各社の変更に追随できるよう設定値にする（RF-DES-001 付録 A.3 注記）。
/// </summary>
public sealed class SocialOptions
{
    public const string SectionName = "Social";

    /// <summary>公式 API の設定がない SNS をデモ接続（モック）で使えるようにする。本番は false。</summary>
    public bool UseMock { get; set; } = true;

    public XOptions X { get; set; } = new();
    public MetaOptions Meta { get; set; } = new();
    public ThreadsOptions Threads { get; set; } = new();
    public LineOptions Line { get; set; } = new();
}

public sealed class XOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string AuthorizeUrl { get; set; } = "https://x.com/i/oauth2/authorize";
    public string ApiBaseUrl { get; set; } = "https://api.x.com/";
    public string Scopes { get; set; } = "tweet.read tweet.write users.read media.write offline.access";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Facebook ページ・Instagram（Facebook Login for Business）。</summary>
public sealed class MetaOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string GraphVersion { get; set; } = "v24.0";
    public string DialogBaseUrl { get; set; } = "https://www.facebook.com/";
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com/";
    public string FacebookScopes { get; set; } =
        "pages_show_list,pages_read_engagement,pages_manage_posts,pages_manage_engagement,pages_messaging,read_insights,business_management";
    public string InstagramScopes { get; set; } =
        "instagram_basic,instagram_content_publish,instagram_manage_insights,instagram_manage_comments,pages_show_list,pages_read_engagement,business_management";

    /// <summary>Facebook 投稿の insights 指標（Meta は指標名を随時廃止・追加するため設定値）。</summary>
    public string FacebookPostMetrics { get; set; } = "post_impressions,post_impressions_unique,post_clicks";

    /// <summary>Instagram メディアの insights 指標。</summary>
    public string InstagramMediaMetrics { get; set; } = "views,reach,likes,comments,shares,saved,profile_visits,follows";
    /// <summary>Webhook 購読確認（hub.verify_token）。</summary>
    public string? WebhookVerifyToken { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

public sealed class ThreadsOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string ApiVersion { get; set; } = "v1.0";
    public string AuthorizeUrl { get; set; } = "https://threads.net/oauth/authorize";
    public string GraphBaseUrl { get; set; } = "https://graph.threads.net/";
    public string Scopes { get; set; } =
        "threads_basic,threads_content_publish,threads_manage_insights,threads_read_replies,threads_manage_replies";

    /// <summary>Threads メディアの insights 指標。</summary>
    public string MediaMetrics { get; set; } = "views,likes,replies,reposts,quotes,shares";
    public string? WebhookVerifyToken { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>LINE はアプリ単位の設定はなく、公式アカウントごとにチャネル ID・シークレットを入力してもらう。</summary>
public sealed class LineOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://api.line.me/";
}
