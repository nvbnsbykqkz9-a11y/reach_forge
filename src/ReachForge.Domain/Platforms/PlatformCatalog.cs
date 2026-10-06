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
            ImageAspect = new(16, 9), ImageSize = (1600, 900), VideoAspect = new(16, 9), MaxVideoSeconds = 140,
            CostPerPostUsd = 0.015m, CostPerPostWithUrlUsd = 0.20m,
            StyleGuide = "140字前後で要点とフックを入れる。ハッシュタグは1〜2個。本文にURLを含めない（リンクはリプライやプロフィールへ誘導）。",
            Note = "URL付き投稿は通常の約13倍の費用（2026/4/20〜）",
        },
        new()
        {
            Platform = SocialPlatform.Instagram, DisplayName = "Instagram", Phase = ReleasePhase.Initial,
            MaxBodyLength = 2200, MaxHashtags = 30, RecommendedHashtags = (5, 15),
            LinkPolicy = LinkPolicy.NotClickable, DailyPostLimit = 100,
            ImageAspect = new(4, 5), ImageSize = (1080, 1350), VideoAspect = new(9, 16), FoldAt = 125,
            StyleGuide = "冒頭125字にフックを置く。改行と絵文字で読みやすくし、保存を促すCTAで締める。本文リンクは無効なので「プロフィールのリンクから」と誘導する。ハッシュタグは5〜15個。",
            Note = "ビジネス／クリエイターアカウントのみ。画像は JPEG（sRGB・8MB以下）",
        },
        new()
        {
            Platform = SocialPlatform.Facebook, DisplayName = "Facebook", Phase = ReleasePhase.Initial,
            MaxBodyLength = 63206, RecommendedHashtags = (0, 3),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 25,
            ImageAspect = new(1, 1), ImageSize = (1200, 1200),
            StyleGuide = "やや長文のストーリー型。ハッシュタグは0〜3個。リンク可（OGPを確認）。",
            Note = "Facebookページのみ（個人プロフィール不可）",
        },
        new()
        {
            Platform = SocialPlatform.Threads, DisplayName = "Threads", Phase = ReleasePhase.Initial,
            MaxBodyLength = 500, MaxHashtags = 1, RecommendedHashtags = (0, 1),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 250,
            ImageAspect = new(4, 5), ImageSize = (1080, 1350), VideoAspect = new(9, 16), MaxVideoSeconds = 300,
            StyleGuide = "500字以内の会話調で、問いかけを入れる。ハッシュタグは1個まで。Instagramのキャプションをそのまま流用しない。",
        },
        new()
        {
            Platform = SocialPlatform.Line, DisplayName = "LINE", Phase = ReleasePhase.Initial,
            MaxBodyLength = 5000, MaxHashtags = 0, RecommendedHashtags = (0, 0),
            LinkPolicy = LinkPolicy.Allowed,
            ImageAspect = new(1, 1), ImageSize = (1040, 1040),
            StyleGuide = "簡潔に特典を訴求する。ハッシュタグは使わない。ボタン・クーポンへの導線を入れる。",
            Note = "友だち登録（オプトイン）済みユーザーのみ配信",
        },
        new()
        {
            Platform = SocialPlatform.TikTok, DisplayName = "TikTok", Phase = ReleasePhase.Phase2,
            MaxBodyLength = 2200, RecommendedHashtags = (3, 5),
            LinkPolicy = LinkPolicy.NotAllowed, DailyPostLimit = 15,
            ImageAspect = new(9, 16), ImageSize = (1080, 1920), VideoAspect = new(9, 16),
            StyleGuide = "動画前提。冒頭2秒のフック文をテロップ用に用意する。ハッシュタグは3〜5個。リンクは入れない。",
            Note = "未監査アプリの投稿は非公開に強制される",
        },
        new()
        {
            Platform = SocialPlatform.YouTube, DisplayName = "YouTube", Phase = ReleasePhase.Phase2,
            MaxBodyLength = 5000, MaxTitleLength = 100, RecommendedHashtags = (3, 3),
            LinkPolicy = LinkPolicy.Allowed, DailyPostLimit = 100,
            ImageAspect = new(16, 9), ImageSize = (1280, 720), VideoAspect = new(9, 16), MaxVideoSeconds = 60,
            StyleGuide = "タイトルは100字以内。説明欄に詳細とタイムスタンプ。ハッシュタグは #Shorts を含め3個。",
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
