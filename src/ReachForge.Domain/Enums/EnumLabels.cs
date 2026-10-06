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
}
