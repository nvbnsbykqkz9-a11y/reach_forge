using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>広告を出す仕組み（SNS の広告マネージャー）。Facebook と Instagram はどちらも Meta。</summary>
public enum AdNetwork : short
{
    /// <summary>お試し（実際には出稿しない）。</summary>
    Demo = 0,
    Meta = 1,
    TikTok = 2,
    X = 3,
    /// <summary>Google 広告（YouTube）。</summary>
    Google = 4,
}

/// <summary>広告の目的（初心者向けに3つにしぼる）。</summary>
public enum AdObjective : short
{
    /// <summary>サイト（お店・商品のページ）に来てほしい。</summary>
    Traffic = 1,
    /// <summary>多くの人に知ってほしい。</summary>
    Awareness = 2,
    /// <summary>動画を見てほしい。</summary>
    VideoViews = 3,
}

public enum AdGender : short { All = 0, Male = 1, Female = 2 }

public enum AdStatus : short
{
    /// <summary>つくりかけ。</summary>
    Draft = 1,
    /// <summary>広告マネージャーへ送っている途中。</summary>
    Submitting = 2,
    /// <summary>SNS 側の審査中。</summary>
    InReview = 3,
    /// <summary>配信中。</summary>
    Active = 4,
    /// <summary>一時停止中。</summary>
    Paused = 5,
    /// <summary>期間が終わった。</summary>
    Completed = 6,
    /// <summary>審査で承認されなかった。</summary>
    Rejected = 7,
    /// <summary>出稿できなかった（広告マネージャーには何も残していない）。</summary>
    Failed = 8,
}

public enum AdAccountStatus : short { Active = 1, NeedsReauth = 2, Disconnected = 3 }

/// <summary>
/// 広告アカウント（各社の広告マネージャーのアカウント）。広告費はこのアカウントに登録された支払い方法へ請求される。
/// トークンは資格情報ストアに保存し、ここには参照キーだけを持つ。
/// </summary>
public sealed class AdAccount : Entity
{
    public Guid WorkspaceId { get; set; }
    public AdNetwork Network { get; set; }

    /// <summary>各社の広告アカウント ID（Meta は act_…、TikTok は advertiser_id など）。</summary>
    public required string ExternalAccountId { get; set; }
    public required string Name { get; set; }

    /// <summary>通貨（JPY など）。予算の単位。</summary>
    public string Currency { get; set; } = "JPY";
    public required string CredentialSecretRef { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public AdAccountStatus Status { get; set; } = AdAccountStatus.Active;
    public bool IsDemo { get; set; }

    /// <summary>各社ごとの追加の値（X の支払い方法 ID、TikTok の表示名 ID など）。</summary>
    public Dictionary<string, string> Extra { get; set; } = [];
}

/// <summary>だれに見せるか（初心者向け：日本全国・年齢・性別）。</summary>
public sealed record AdTargeting
{
    public const int MinAge = 18;
    public const int MaxAge = 65;

    public int AgeMin { get; init; } = MinAge;

    /// <summary>上限（65 は「65歳以上」を含む）。</summary>
    public int AgeMax { get; init; } = MaxAge;
    public AdGender Gender { get; init; } = AdGender.All;

    /// <summary>配信する国（ISO 3166-1）。今は日本だけ。</summary>
    public string Country { get; init; } = "JP";
}

/// <summary>広告の中身（文章・リンク・画像または動画）。</summary>
public sealed record AdCreative
{
    /// <summary>本文（メインのテキスト）。</summary>
    public string PrimaryText { get; init; } = "";

    /// <summary>見出し。</summary>
    public string Headline { get; init; } = "";

    /// <summary>説明（任意）。</summary>
    public string Description { get; init; } = "";

    /// <summary>リンク先（お店・商品のページ）。</summary>
    public string? LinkUrl { get; init; }

    /// <summary>ボタン（LEARN_MORE・SHOP_NOW など、各社共通の名前）。</summary>
    public string CallToAction { get; init; } = "LEARN_MORE";

    public Guid? MediaAssetId { get; init; }

    /// <summary>YouTube の広告に使う動画（投稿済みの動画の ID）。</summary>
    public string? YouTubeVideoId { get; init; }
}

/// <summary>配信の成果（各社から取得した値。通貨は広告アカウントの通貨）。</summary>
public sealed record AdResults(long Impressions, long Clicks, decimal Spend, long Reach, long VideoViews, DateTimeOffset UpdatedAt);

/// <summary>
/// 有料広告（1つの SNS・1つの広告）。各社の広告マネージャーに キャンペーン → 広告セット（予算・期間・対象）→ 広告 をつくる。
/// 送るときはすべて停止した状態でつくり、全部できてから配信を始める（途中で失敗したらつくった分を消す）。
/// </summary>
public sealed class AdCampaign : Entity
{
    public const int MaxDays = 90;

    public Guid WorkspaceId { get; set; }
    public Guid AdAccountId { get; set; }
    public AdNetwork Network { get; set; }
    public SocialPlatform Platform { get; set; }
    public required string Name { get; set; }
    public AdObjective Objective { get; set; } = AdObjective.Traffic;

    /// <summary>1日の予算（広告アカウントの通貨）。</summary>
    public decimal DailyBudget { get; set; }
    public string Currency { get; set; } = "JPY";
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public AdTargeting Targeting { get; set; } = new();
    public AdCreative Creative { get; set; } = new();
    public AdStatus Status { get; private set; } = AdStatus.Draft;

    /// <summary>各社でつくったもの（campaign・adset・ad などの ID）。</summary>
    public Dictionary<string, string> ExternalIds { get; private set; } = [];
    public string? LastError { get; private set; }

    /// <summary>審査で承認されなかった理由など、各社からの説明。</summary>
    public string? ReviewNote { get; private set; }
    public AdResults? Results { get; private set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? SubmittedAt { get; private set; }

    public int Days => Math.Max(1, (int)Math.Ceiling((EndAt - StartAt).TotalDays));

    /// <summary>広告費の上限の目安（1日の予算 × 日数）。実際の請求は各社の配信結果による。</summary>
    public decimal MaxTotalSpend => DailyBudget * Days;

    public bool IsLive => Status is AdStatus.InReview or AdStatus.Active or AdStatus.Paused;

    /// <summary>送る前の確認（予算・期間・中身）。</summary>
    public IReadOnlyList<string> Validate(DateTimeOffset now, decimal minDailyBudget)
    {
        var errors = new List<string>();
        if (DailyBudget < minDailyBudget) errors.Add($"1日の予算は {minDailyBudget:N0} {Currency} 以上にしてください。");
        if (StartAt < now.AddMinutes(-10)) errors.Add("開始日時が過去になっています。");
        if (EndAt <= StartAt) errors.Add("終了日時は開始日時より後にしてください。");
        if (EndAt - StartAt > TimeSpan.FromDays(MaxDays)) errors.Add($"期間は{MaxDays}日までです。");
        if (Targeting.AgeMin < AdTargeting.MinAge || Targeting.AgeMax > AdTargeting.MaxAge || Targeting.AgeMin > Targeting.AgeMax)
        {
            errors.Add($"年齢は{AdTargeting.MinAge}〜{AdTargeting.MaxAge}歳の範囲で選んでください。");
        }
        if (string.IsNullOrWhiteSpace(Creative.PrimaryText)) errors.Add("広告の本文を入力してください。");
        if (Objective == AdObjective.Traffic && string.IsNullOrWhiteSpace(Creative.LinkUrl)) errors.Add("「サイトに来てほしい」には、リンク先の URL が必要です。");
        if (Creative.LinkUrl is { Length: > 0 } url && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
        {
            errors.Add("リンク先は https:// で始まる URL にしてください。");
        }
        return errors;
    }

    public void MarkSubmitting(DateTimeOffset now)
    {
        if (Status is not (AdStatus.Draft or AdStatus.Failed)) throw new DomainException(ErrorCodes.Validation, "この広告はすでに出稿しています。");
        Status = AdStatus.Submitting;
        SubmittedAt = now;
        LastError = null;
    }

    public void MarkSubmitted(IReadOnlyDictionary<string, string> externalIds, AdStatus status)
    {
        ExternalIds = new Dictionary<string, string>(externalIds);
        Status = status;
        LastError = null;
    }

    /// <summary>出稿に失敗した（各社につくった分は消してある）。下書きに戻して直せるようにする。</summary>
    public void MarkFailed(string error)
    {
        Status = AdStatus.Failed;
        LastError = error;
        ExternalIds = [];
    }

    /// <summary>各社の状態・成果を反映する。</summary>
    public void Sync(AdStatus status, AdResults? results, string? reviewNote)
    {
        if (!IsLive && Status != AdStatus.Completed) return;
        Status = status;
        Results = results ?? Results;
        ReviewNote = reviewNote;
    }

    public void SetPaused(bool paused)
    {
        if (!IsLive) throw new DomainException(ErrorCodes.Validation, "この広告は配信していません。");
        Status = paused ? AdStatus.Paused : AdStatus.Active;
    }
}
