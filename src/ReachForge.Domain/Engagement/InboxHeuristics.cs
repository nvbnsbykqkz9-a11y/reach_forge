using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Engagement;

public sealed record InboxLabels(Sentiment Sentiment, InboxIntent Intent, Urgency Urgency, SensitiveTopic Sensitive, string Language);

/// <summary>
/// キーワードによる分類（AI が使えないときの代替と、ローカル用スタブ）。
/// 迷ったら人の対応に寄せる（クレーム・医療・法務の語があれば「人が対応」）。
/// </summary>
public static class InboxHeuristics
{
    private static readonly string[] s_complaint = ["届いていません", "届かない", "返金", "最悪", "ひどい", "二度と", "クレーム", "対応が悪", "まずかった", "不快", "怒", "がっかり", "異物", "遅すぎ"];
    private static readonly string[] s_medical = ["アレルギー", "体調", "腹痛", "食中毒", "病院", "吐き", "じんましん", "妊娠中"];
    private static readonly string[] s_legal = ["訴え", "弁護士", "法的", "消費者センター", "警察", "著作権", "訴訟", "個人情報"];
    private static readonly string[] s_spam = ["フォロワーを", "副業", "稼げ", "DMください", "無料プレゼント", "仮想通貨", "相互フォロー", "bit.ly"];
    private static readonly string[] s_reservation = ["予約", "席", "貸切", "取り置き"];
    private static readonly string[] s_purchase = ["買えます", "購入", "注文", "値段", "価格", "いくら", "在庫", "通販", "テイクアウト", "持ち帰り"];
    private static readonly string[] s_praise = ["美味しかった", "おいしかった", "最高", "すてき", "素敵", "ありがとう", "大好き", "かわいい", "行きます", "楽しみ"];
    private static readonly string[] s_negative = ["残念", "微妙", "高い", "嫌", "不満", "困", "どうなって"];

    public static InboxLabels Classify(string text)
    {
        bool Any(string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        var sensitive = Any(s_legal) ? SensitiveTopic.Legal
            : Any(s_medical) ? SensitiveTopic.Medical
            : Any(s_complaint) ? SensitiveTopic.Complaint
            : SensitiveTopic.None;
        var isQuestion = text.Contains('？') || text.Contains('?') || text.EndsWith("か", StringComparison.Ordinal);
        var intent = Any(s_spam) && sensitive == SensitiveTopic.None ? InboxIntent.Spam
            : sensitive == SensitiveTopic.Complaint ? InboxIntent.Complaint
            : Any(s_reservation) ? InboxIntent.Reservation
            : Any(s_purchase) ? InboxIntent.Purchase
            : Any(s_praise) && !isQuestion ? InboxIntent.Praise
            : isQuestion ? InboxIntent.Question
            : InboxIntent.Other;
        var sentiment = sensitive != SensitiveTopic.None || Any(s_negative) ? Sentiment.Negative
            : Any(s_praise) ? Sentiment.Positive
            : Sentiment.Neutral;
        var urgency = sensitive != SensitiveTopic.None ? Urgency.High
            : intent is InboxIntent.Question or InboxIntent.Purchase or InboxIntent.Reservation ? Urgency.Medium
            : Urgency.Low;
        var language = text.Any(c => c is >= 'ぁ' and <= 'ヿ' or >= '一' and <= '龯') ? "ja" : "en";
        return new InboxLabels(sentiment, intent, urgency, sensitive, language);
    }
}
