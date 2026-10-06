namespace ReachForge.Domain.Enums;

/// <summary>画面表示用の日本語ラベル。専門用語を避ける（RF-UX-001 1.2 原則5）。</summary>
public static class EnumLabels
{
    public static string ToLabel(this VariantStatus s) => s switch
    {
        VariantStatus.Draft => "下書き",
        VariantStatus.InReview => "承認待ち",
        VariantStatus.Approved => "承認済み",
        VariantStatus.Scheduled => "予約済み",
        VariantStatus.Publishing => "公開中",
        VariantStatus.Published => "公開済み",
        VariantStatus.Failed => "失敗",
        VariantStatus.Canceled => "取り消し",
        VariantStatus.OnHold => "保留",
        _ => s.ToString(),
    };

    public static string ToLabel(this PostObjective o) => o switch
    {
        PostObjective.Awareness => "認知",
        PostObjective.Engagement => "反応",
        PostObjective.Traffic => "集客",
        PostObjective.Conversion => "販売",
        _ => o.ToString(),
    };

    public static string ToLabel(this ChannelStatus s) => s switch
    {
        ChannelStatus.Active => "正常",
        ChannelStatus.ReauthRequired => "要再接続",
        ChannelStatus.Revoked => "切断",
        ChannelStatus.Error => "エラー",
        _ => s.ToString(),
    };

    public static string ToLabel(this Role r) => r switch
    {
        Role.Owner => "オーナー",
        Role.Admin => "管理者",
        Role.Editor => "編集者",
        Role.Approver => "承認者",
        Role.Responder => "返信担当",
        Role.Viewer => "閲覧者",
        _ => r.ToString(),
    };

    public static string ToLabel(this CopyFramework f) => f switch
    {
        CopyFramework.Auto => "おまかせ",
        CopyFramework.Aida => "AIDA（注目→興味→欲求→行動）",
        CopyFramework.Pas => "PAS（問題→共感→解決）",
        CopyFramework.Story => "ストーリー",
        CopyFramework.HowTo => "ハウツー",
        CopyFramework.List => "リスト型",
        _ => f.ToString(),
    };

    public static string ToLabel(this ImageStyle s) => s switch
    {
        ImageStyle.Photo => "写真調",
        ImageStyle.Illustration => "イラスト",
        ImageStyle.Flat => "フラット",
        ImageStyle.ThreeD => "3D",
        _ => s.ToString(),
    };

    public static string ToLabel(this AspectMethod m) => m switch
    {
        AspectMethod.SmartCrop => "自動トリミング",
        AspectMethod.Pad => "余白を付ける",
        AspectMethod.Outpaint => "AIで広げる",
        _ => m.ToString(),
    };

    public static string ToLabel(this AiJobStage s) => s switch
    {
        AiJobStage.Waiting => "待機中",
        AiJobStage.Generating => "生成中",
        AiJobStage.Checking => "確認中",
        AiJobStage.Done => "完了",
        _ => s.ToString(),
    };

    public static string ToLabel(this Entities.InboxIntent i) => i switch
    {
        Entities.InboxIntent.Question => "質問",
        Entities.InboxIntent.Purchase => "購入",
        Entities.InboxIntent.Reservation => "予約",
        Entities.InboxIntent.Complaint => "苦情",
        Entities.InboxIntent.Praise => "称賛",
        Entities.InboxIntent.Spam => "スパム",
        _ => "その他",
    };

    public static string ToLabel(this Entities.Sentiment s) => s switch
    {
        Entities.Sentiment.Positive => "好意的",
        Entities.Sentiment.Negative => "不満",
        _ => "ふつう",
    };

    public static string ToLabel(this Entities.Urgency u) => u switch
    {
        Entities.Urgency.High => "急ぎ",
        Entities.Urgency.Medium => "ふつう",
        _ => "低",
    };

    public static string ToLabel(this Entities.SensitiveTopic t) => t switch
    {
        Entities.SensitiveTopic.Complaint => "苦情・トラブル",
        Entities.SensitiveTopic.Medical => "健康・医療",
        Entities.SensitiveTopic.Legal => "法律・権利",
        _ => "なし",
    };

    public static string ToLabel(this Entities.InboxStatus s) => s switch
    {
        Entities.InboxStatus.New => "未対応",
        Entities.InboxStatus.InProgress => "対応中",
        Entities.InboxStatus.Replied => "返信済み",
        Entities.InboxStatus.Closed => "完了",
        Entities.InboxStatus.Hidden => "非表示",
        _ => s.ToString(),
    };

    public static string ToLabel(this Entities.AbVariable v) => v switch
    {
        Entities.AbVariable.Hook => "書き出し",
        Entities.AbVariable.Image => "画像",
        Entities.AbVariable.Cta => "行動の呼びかけ（CTA）",
        Entities.AbVariable.TimeSlot => "投稿時間",
        _ => v.ToString(),
    };

    public static string ToLabel(this Entities.CampaignKpi k) => k switch
    {
        Entities.CampaignKpi.Impressions => "表示回数",
        Entities.CampaignKpi.EngagementRate => "反応の割合",
        Entities.CampaignKpi.LinkClicks => "リンクのクリック",
        Entities.CampaignKpi.Followers => "フォロワー増加",
        _ => k.ToString(),
    };

    public static string ToLabel(this Entities.AbTestStatus s) => s switch
    {
        Entities.AbTestStatus.Draft => "準備中",
        Entities.AbTestStatus.Running => "実施中",
        Entities.AbTestStatus.Completed => "判定済み",
        Entities.AbTestStatus.Canceled => "中止",
        _ => s.ToString(),
    };
}
