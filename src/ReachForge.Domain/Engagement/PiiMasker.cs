using System.Text.RegularExpressions;

namespace ReachForge.Domain.Engagement;

/// <summary>
/// AI へ送る前の個人情報マスク（F-09 処理 1）。メールアドレス・電話番号・郵便番号・カード番号らしき数字列・
/// SNS のアカウント名を置き換える。原文は DB にのみ保持する。
/// </summary>
public static partial class PiiMasker
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){13,16}(?!\d)")]
    private static partial Regex Card();

    [GeneratedRegex(@"(?<!\d)(?:\+81[\- ]?|0)\d{1,4}[\- ]?\d{1,4}[\- ]?\d{3,4}(?!\d)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"〒?\s?(?<!\d)\d{3}-\d{4}(?!\d)")]
    private static partial Regex Postal();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])@[A-Za-z0-9_.]{2,30}")]
    private static partial Regex Handle();

    public static string Mask(string text)
    {
        var s = Email().Replace(text, "[メール]");
        s = Card().Replace(s, "[番号]");
        s = Phone().Replace(s, "[電話番号]"); // 郵便番号より先に（電話番号の一部を郵便番号と誤認しない）
        s = Postal().Replace(s, "[郵便番号]");
        s = Handle().Replace(s, "[アカウント]");
        return s;
    }
}
