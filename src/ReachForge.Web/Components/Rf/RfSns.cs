using ReachForge.Domain.Enums;

namespace ReachForge.Web.Components.Rf;

/// <summary>SNS の見分け方（色）と、できたものを手動でアップロードする先。</summary>
public static class RfSns
{
    /// <summary>目印の色（各社のロゴではなく、見分けるための色）。</summary>
    public static string Color(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram => "#C13584",
        SocialPlatform.X => "#0F1419",
        SocialPlatform.Facebook => "#1877F2",
        SocialPlatform.Threads => "#5F687A",
        SocialPlatform.TikTok => "#FE2C55",
        SocialPlatform.YouTube => "#E00000",
        SocialPlatform.Line => "#06C755",
        _ => "#5F687A",
    };

    /// <summary>ふつうの投稿をするところ。</summary>
    public static string PostUrl(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram => "https://www.instagram.com/",
        SocialPlatform.X => "https://x.com/compose/post",
        SocialPlatform.Facebook => "https://www.facebook.com/",
        SocialPlatform.Threads => "https://www.threads.net/",
        SocialPlatform.TikTok => "https://www.tiktok.com/upload",
        SocialPlatform.YouTube => "https://studio.youtube.com/",
        SocialPlatform.Line => "https://manager.line.biz/",
        _ => "",
    };

    /// <summary>広告を出すところ（各社の広告マネージャー）。</summary>
    public static string AdsUrl(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram or SocialPlatform.Facebook or SocialPlatform.Threads => "https://adsmanager.facebook.com/",
        SocialPlatform.X => "https://ads.x.com/",
        SocialPlatform.TikTok => "https://ads.tiktok.com/",
        SocialPlatform.YouTube => "https://ads.google.com/",
        SocialPlatform.Line => "https://admanager.line.biz/",
        _ => "",
    };

    public static string AdsName(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram or SocialPlatform.Facebook or SocialPlatform.Threads => "Meta 広告マネージャ",
        SocialPlatform.X => "X 広告",
        SocialPlatform.TikTok => "TikTok 広告マネージャー",
        SocialPlatform.YouTube => "Google 広告",
        SocialPlatform.Line => "LINE 広告",
        _ => "広告マネージャー",
    };
}
