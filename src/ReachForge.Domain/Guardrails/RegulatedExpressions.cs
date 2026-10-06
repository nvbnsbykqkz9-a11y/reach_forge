namespace ReachForge.Domain.Guardrails;

/// <summary>
/// 規制表現辞書の初期値。法令対応の詳細判断は専門家の監修を前提とし（RF-DES-001 9.2 / 付録B No.5）、
/// 運用で辞書を更新する（週次レビュー）。ここでは代表的な表現と言い換え案のみを持つ。
/// </summary>
public static class RegulatedExpressions
{
    /// <summary>景品表示法（優良・有利誤認）：根拠資料が必要な表現 → 言い換え案。長い語を先に評価する。</summary>
    public static readonly IReadOnlyList<(string Term, string Suggestion)> Premiums =
    [
        ("一番おいしい", "自信作の"),
        ("業界初", "新しい"),
        ("業界最安", "お求めやすい価格"),
        ("最安値", "お求めやすい価格"),
        ("最安", "お求めやすい価格"),
        ("No.1", "多くのお客様に選ばれている"),
        ("No1", "多くのお客様に選ばれている"),
        ("ナンバーワン", "多くのお客様に選ばれている"),
        ("日本一", "こだわりの"),
        ("世界一", "こだわりの"),
        ("史上最高", "自信をもっておすすめする"),
        ("絶対", "きっと"),
        ("100%", "たっぷり"),
    ];

    /// <summary>医薬品医療機器等法（薬機法）：化粧品・健康食品等で効能効果を逸脱する表現 → 言い換え案。</summary>
    public static readonly IReadOnlyList<(string Term, string Suggestion)> Pharma =
    [
        ("シミが消える", "肌を健やかに保つ"),
        ("シワが消える", "肌にうるおいを与える"),
        ("アンチエイジング", "エイジングケア（年齢に応じたお手入れ）"),
        ("若返る", "いきいきとした印象に"),
        ("痩せる", "すっきりとした毎日に"),
        ("やせる", "すっきりとした毎日に"),
        ("治る", "すこやかな毎日をサポート"),
        ("治す", "ケアする"),
        ("効く", "うれしい"),
        ("免疫力アップ", "毎日の健康づくりに"),
        ("予防", "毎日のケアに"),
    ];

    /// <summary>薬機法の辞書を適用する業種キーワード（F-02 業務ルール）。</summary>
    public static readonly IReadOnlyList<string> PharmaIndustries =
        ["化粧品", "コスメ", "健康食品", "サプリ", "医療", "医薬", "美容", "エステ", "cosmetic", "supplement", "health"];

    /// <summary>ステマ規制の表記として認める語。</summary>
    public static readonly IReadOnlyList<string> PrDisclosures = ["#PR", "＃PR", "#広告", "【PR】", "[PR]", "PR：", "PR:", "広告", "プロモーション"];

    public static bool IsPharmaIndustry(string industry) =>
        PharmaIndustries.Any(k => industry.Contains(k, StringComparison.OrdinalIgnoreCase));
}
