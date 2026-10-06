using System.Globalization;
using System.Text;
using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Engagement;

public sealed record KnowledgeHit(KnowledgeEntry Entry, double Score);

/// <summary>
/// FAQ の検索（RAG の検索部分）。日本語の分かち書きをせずに使える文字 2-gram の重なりで採点する。
/// 件数が増えたら埋め込みベクトル検索（pgvector / AI Search）へ差し替える。
/// </summary>
public static class KnowledgeMatcher
{
    /// <summary>自動返信に使う一致度の下限（誤答を避けるため高めにする）。</summary>
    public const double AutoReplyThreshold = 0.45;
    public const double SuggestThreshold = 0.12;

    public static IReadOnlyList<KnowledgeHit> Search(string query, IEnumerable<KnowledgeEntry> entries, int take = 3)
    {
        var q = Grams(query);
        if (q.Count == 0) return [];
        return entries
            .Select(e =>
            {
                var question = Grams(e.Question);
                var all = Grams(e.Question + " " + e.Answer);
                // 質問文との一致を重く、回答文との一致を軽く見る
                var score = 0.75 * Overlap(q, question) + 0.25 * Overlap(q, all);
                return new KnowledgeHit(e, Math.Round(score, 3));
            })
            .Where(h => h.Score >= SuggestThreshold)
            .OrderByDescending(h => h.Score)
            .Take(take)
            .ToList();
    }

    /// <summary>クエリ側の 2-gram のうち、文書に含まれる割合。</summary>
    private static double Overlap(HashSet<string> query, HashSet<string> doc) =>
        query.Count == 0 ? 0 : (double)query.Count(doc.Contains) / query.Count;

    private static readonly HashSet<string> s_stop = ["です", "ます", "すか", "した", "ください", "って", "には", "から", "ので", "けど", "まし", "でし"];

    internal static HashSet<string> Grams(string text)
    {
        var normalized = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || cat == UnicodeCategory.OtherLetter) normalized.Append(ch);
            else normalized.Append(' ');
        }
        var grams = new HashSet<string>();
        foreach (var word in normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length == 1) continue;
            for (var i = 0; i + 1 < word.Length; i++)
            {
                var g = word.Substring(i, 2);
                if (!s_stop.Contains(g) && !IsKanaParticle(g)) grams.Add(g);
            }
        }
        return grams;
    }

    /// <summary>ひらがなだけの 2-gram（助詞・語尾）は意味が薄いため除く。</summary>
    private static bool IsKanaParticle(string g) => g.All(c => c is >= 'ぁ' and <= 'ゖ');
}
