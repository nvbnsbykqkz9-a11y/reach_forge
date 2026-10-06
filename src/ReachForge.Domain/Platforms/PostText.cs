using System.Globalization;
using System.Text.RegularExpressions;

namespace ReachForge.Domain.Platforms;

/// <summary>投稿本文の解析ユーティリティ（文字数・ハッシュタグ・URL）。</summary>
public static partial class PostText
{
    [GeneratedRegex(@"https?://[^\s　]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<=^|[\s　])[#＃]([\p{L}\p{Mn}\p{Nd}_ー]+)")]
    private static partial Regex HashtagRegex();

    /// <summary>
    /// 文字数（書記素クラスタ単位）。絵文字や結合文字を1文字として数える。
    /// 注：X は全角を2カウントする重み付けだが、ここでは UI 表示（RF-UX-001 5.2）と同じ見た目の文字数を返す。
    /// </summary>
    public static int Length(string text) => new StringInfo(text).LengthInTextElements;

    public static IReadOnlyList<string> Urls(string text) =>
        UrlRegex().Matches(text).Select(m => m.Value).ToList();

    public static bool ContainsUrl(string text) => UrlRegex().IsMatch(text);

    public static string RemoveUrls(string text) =>
        UrlRegex().Replace(text, "").Replace("  ", " ").Trim();

    /// <summary>本文中のハッシュタグ（# なし）。</summary>
    public static IReadOnlyList<string> Hashtags(string text) =>
        HashtagRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    /// <summary>本文とハッシュタグ一覧を合成した投稿テキスト。本文に既にあるタグは重複させない。</summary>
    public static string Compose(string body, IEnumerable<string> hashtags)
    {
        var existing = new HashSet<string>(Hashtags(body), StringComparer.OrdinalIgnoreCase);
        var tags = hashtags
            .Select(Normalize)
            .Where(t => t.Length > 0 && existing.Add(t))
            .Select(t => "#" + t)
            .ToList();
        return tags.Count == 0 ? body.TrimEnd() : $"{body.TrimEnd()}\n\n{string.Join(' ', tags)}";
    }

    public static string Normalize(string hashtag) => hashtag.Trim().TrimStart('#', '＃').Replace(" ", "");

    /// <summary>書記素クラスタ単位で切り詰める（末尾に省略記号を付ける）。</summary>
    public static string Truncate(string text, int maxLength)
    {
        if (Length(text) <= maxLength) return text;
        var si = new StringInfo(text);
        return si.SubstringByTextElements(0, Math.Max(0, maxLength - 1)).TrimEnd() + "…";
    }
}
