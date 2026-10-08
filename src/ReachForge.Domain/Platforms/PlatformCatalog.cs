using System.Collections.Frozen;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Platforms;

/// <summary>
/// プラットフォーム制約マスタの初期値（RF-DES-001 5.2 / 5.4 / F-06）。
/// 実装着手時点の調査値であり、各社公式ドキュメントでの再確認を前提とする。
/// </summary>
public static class PlatformCatalog
{
    private static readonly FrozenDictionary<SocialPlatform, PlatformConstraint> s_all = new PlatformConstraint[]
    {
        new()
        {
            Platform = SocialPlatform.X, DisplayName = "X", Phase = ReleasePhase.Initial,
            MaxBodyLength = 280, TargetBodyLength = 140, RecommendedHashtags = (1, 2),
            LinkPolicy = LinkPolicy.DiscouragedByCost,
            ImageAspect = new(1, 1), ImageSize = (1200, 1200), VideoAspect = new(16, 9), MaxVideoSeconds = 140,
            ImageFormats = [new("square", "画像広告・投稿（正方形）", new(1, 1), 1200, 1200), new("wide", "投稿（横長）", new(16, 9), 1600, 900)],
            VideoFormats = [new("timeline", "タイムライン動画（横型）", new(16, 9), 1920, 1080, 140)],
            CostPerPostUsd = 0.015m, CostPerPostWithUrlUsd = 0.20m, MaxImages = 4,
            StyleGuide = "140字前後で要点とフックを入れる。ハッシュタグは1〜2個。本文にURLを含めない（リンクはリプライやプロフィールへ誘導）。",
            Note = "URL付き投稿は通常の約13倍の費用（2026/4/20〜）",
        },
        new()
        {
            Platform = SocialPlatform.Instagram, DisplayName = "Instagram", Phase = ReleasePhase.Initial,
            MaxBodyLength = 2200, MaxHashtags = 30, RecommendedHashtags = (5, 15),
            LinkPolicy = LinkPolicy.NotClickable, DailyPostLimit = 100,
            ImageAspect = new(4, 5), ImageSize = (1080, 1350), VideoAspect = new(9, 16), FoldAt = 125, MaxImages = 10, MaxVideoSeconds = 90,
            ImageFormats = [new("feed", "フィード", new(4, 5), 1080, 1350), new("story", "ストーリーズ", new(9, 16), 1080, 1920)],
            VideoFormats = [new("reels", "リール・ストーリーズ", new(9, 16), 1080, 1920, 90)],
            StyleGuide = "冒頭125字にフックを置く。改行と絵文字で読みやすくし、保存を促すCTAで締める。本文リンクは無効なので「プロフィールのリンクから」と誘導する。ハッシュタグは5〜15個。",
            Note = "ビジネス／クリエイターアカウントのみ。画像は JPEG（sRGB・8MB以下）",
        },
        new()
        {
            Platform = SocialPlatform.Facebook, DisplayName = "Facebook", Phase = ReleasePhase.Initial,
            MaxBodyLength = 63206, RecommendedHashtags = (0, 3),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 25,
            ImageAspect = new(4, 5), ImageSize = (1080, 1350), VideoAspect = new(9, 16), MaxImages = 10, MaxVideoSeconds = 90,
            ImageFormats = [new("feed", "フィード", new(4, 5), 1080, 1350), new("link", "リンク広告", new(191, 100), 1200, 628)],
            VideoFormats = [new("reels", "リール・ストーリーズ", new(9, 16), 1080, 1920, 90)],
            StyleGuide = "やや長文のストーリー型。ハッシュタグは0〜3個。リンク可（OGPを確認）。",
            Note = "Facebookページのみ（個人プロフィール不可）",
        },
        new()
        {
            Platform = SocialPlatform.Threads, DisplayName = "Threads", Phase = ReleasePhase.Initial,
            MaxBodyLength = 500, MaxHashtags = 1, RecommendedHashtags = (0, 1),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 250,
            ImageAspect = new(4, 5), ImageSize = (1080, 1350), VideoAspect = new(9, 16), MaxVideoSeconds = 300, MaxImages = 10,
            ImageFormats = [new("post", "投稿", new(4, 5), 1080, 1350)],
            VideoFormats = [new("post", "投稿（縦型）", new(9, 16), 1080, 1920, 300)],
            StyleGuide = "500字以内の会話調で、問いかけを入れる。ハッシュタグは1個まで。Instagramのキャプションをそのまま流用しない。",
        },
        new()
        {
            Platform = SocialPlatform.Line, DisplayName = "LINE", Phase = ReleasePhase.Initial,
            MaxBodyLength = 5000, MaxHashtags = 0, RecommendedHashtags = (0, 0),
            LinkPolicy = LinkPolicy.Allowed,
            ImageAspect = new(1, 1), ImageSize = (1080, 1080), VideoAspect = new(9, 16), MaxImages = 4, MaxVideoSeconds = 600, // 1回の送信は最大5メッセージ（本文＋画像4枚）
            ImageFormats = [new("square", "LINE広告（Square）・メッセージ", new(1, 1), 1080, 1080), new("card", "LINE広告（Card）", new(191, 100), 1200, 628)],
            VideoFormats = [new("vertical", "LINE広告（Vertical）・VOOM", new(9, 16), 1080, 1920, 600), new("card", "LINE広告（Card 動画）", new(16, 9), 1920, 1080, 600)],
            StyleGuide = "簡潔に特典を訴求する。ハッシュタグは使わない。ボタン・クーポンへの導線を入れる。",
            Note = "友だち登録（オプトイン）済みユーザーのみ配信",
        },
        new()
        {
            Platform = SocialPlatform.TikTok, DisplayName = "TikTok", Phase = ReleasePhase.Initial,
            MaxBodyLength = 2200, RecommendedHashtags = (3, 5),
            LinkPolicy = LinkPolicy.NotAllowed, DailyPostLimit = 15,
            ImageAspect = new(9, 16), ImageSize = (1080, 1920), VideoAspect = new(9, 16), MaxVideoSeconds = 600, MaxImages = 10,
            ImageFormats = [new("photo", "フォトモード", new(9, 16), 1080, 1920)],
            VideoFormats = [new("video", "動画", new(9, 16), 1080, 1920, 600)],
            StyleGuide = "動画前提。冒頭2秒のフック文をテロップ用に用意する。ハッシュタグは3〜5個。リンクは入れない。",
            Note = "動画または写真が必要。審査前のアプリの投稿は「自分のみ」に強制される",
        },
        new()
        {
            Platform = SocialPlatform.YouTube, DisplayName = "YouTube", Phase = ReleasePhase.Initial,
            MaxBodyLength = 5000, MaxTitleLength = 100, RecommendedHashtags = (3, 3),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 6, VideoOnly = true,
            ImageAspect = new(16, 9), ImageSize = (1280, 720), VideoAspect = new(9, 16), MaxVideoSeconds = 180,
            ImageFormats = [new("thumbnail", "サムネイル", new(16, 9), 1280, 720)],
            VideoFormats = [new("shorts", "ショート（3分以内）", new(9, 16), 1080, 1920, 180), new("video", "通常の動画（横型）", new(16, 9), 1920, 1080)],
            StyleGuide = "タイトルは100字以内。説明欄に詳細とタイムスタンプ。ハッシュタグは #Shorts を含め3個。",
            Note = "動画のみ。縦型・3分以内はショートとして公開。アップロードは API の1日の割り当てを多く使うため、既定の割り当てでは1日数本まで",
        },
        new()
        {
            Platform = SocialPlatform.LinkedIn, DisplayName = "LinkedIn", Phase = ReleasePhase.Phase2,
            MaxBodyLength = 3000, RecommendedHashtags = (3, 5),
            LinkPolicy = LinkPolicy.Allowed,
            ImageAspect = new(1, 1), ImageSize = (1200, 1200),
            StyleGuide = "ビジネス視点で、実績や学びを中心に書く。ハッシュタグは3〜5個。",
            Note = "パートナープログラム承認が前提。会社ページ必須",
        },
        new()
        {
            Platform = SocialPlatform.Pinterest, DisplayName = "Pinterest", Phase = ReleasePhase.Phase2,
            MaxBodyLength = 500, MaxTitleLength = 100, RecommendedHashtags = (0, 5),
            LinkPolicy = LinkPolicy.Required,
            ImageAspect = new(2, 3), ImageSize = (1000, 1500), VideoAspect = new(9, 16),
            StyleGuide = "検索キーワードを含むタイトルと説明にする。遷移先URLを必ず設定する。",
        },
    }.ToFrozenDictionary(c => c.Platform);

    public static PlatformConstraint Get(SocialPlatform platform) => s_all[platform];

    public static IEnumerable<PlatformConstraint> All => s_all.Values.OrderBy(c => c.Platform);

    public static IEnumerable<PlatformConstraint> InitialRelease => All.Where(c => c.Phase == ReleasePhase.Initial);
}
