using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Web.Components.Rf;

/// <summary>「つくる」メニューの SNS（並び・色・初心者向けの説明）。</summary>
public static class RfSns
{
    private static readonly SocialPlatform[] s_order =
    [
        SocialPlatform.Instagram, SocialPlatform.X, SocialPlatform.Facebook, SocialPlatform.Threads,
        SocialPlatform.TikTok, SocialPlatform.YouTube, SocialPlatform.Line,
    ];

    public static IReadOnlyList<PlatformConstraint> Creatable { get; } = s_order.Select(PlatformCatalog.Get).ToList();

    public static string Slug(SocialPlatform p) => p.ToString().ToLowerInvariant();

    public static string CreateHref(SocialPlatform p) => $"/create/{Slug(p)}";

    public static SocialPlatform? FromSlug(string? slug) =>
        s_order.Cast<SocialPlatform?>().FirstOrDefault(p => string.Equals(Slug(p!.Value), slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>メニューの目印の色（各社のロゴではなく、見分けるための色）。</summary>
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

    /// <summary>その SNS で集客しやすい使い方（ひとこと）。</summary>
    public static string Intro(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram => "写真や短い動画で、お店や商品の魅力を見せるのが得意です。",
        SocialPlatform.X => "短い文章で、今日のお知らせや最新情報をすばやく届けられます。",
        SocialPlatform.Facebook => "地域の人やお客様に、少し長めの文章でお知らせを届けられます。",
        SocialPlatform.Threads => "会話のような短い文章で、お客様とのやりとりを広げられます。",
        SocialPlatform.TikTok => "縦型の短い動画で、まだお店を知らない人に見つけてもらえます。",
        SocialPlatform.YouTube => "ショート動画で、商品やサービスの様子を伝えられます。",
        SocialPlatform.Line => "友だち登録してくれたお客様に、お知らせを直接届けられます。",
        _ => "",
    };

    /// <summary>画像・動画が必要か。</summary>
    public static bool NeedsMedia(SocialPlatform p) => p is SocialPlatform.Instagram or SocialPlatform.TikTok or SocialPlatform.YouTube;

    /// <summary>画像・動画についての案内。</summary>
    public static string MediaRule(SocialPlatform p)
    {
        var c = PlatformCatalog.Get(p);
        return p switch
        {
            SocialPlatform.YouTube => "YouTube には動画が必要です。縦型・3分以内の動画はショートになります。",
            SocialPlatform.TikTok => "TikTok には動画（または写真）が必要です。縦型の動画がおすすめです。",
            SocialPlatform.Instagram => $"Instagram には画像か動画が必要です（画像は{c.MaxImages}枚まで）。",
            _ => $"画像・動画はなくても投稿できますが、あると目に留まりやすくなります（画像は{c.MaxImages}枚まで）。",
        };
    }
}
