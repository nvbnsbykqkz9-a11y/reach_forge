using System.Globalization;
using System.Text.RegularExpressions;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Domain.Guardrails;

/// <summary>
/// ルールベースのガードレール（RF-DES-001 4.6 制約遵守・9.2 法令対応）。
/// AI による事実性・安全性判定（Compliance Agent / Content Safety）の前段として、決定的に判定できるものを扱う。
/// 指摘文は「責めない」言い方で、理由と代わりの案を必ずセットにする（RF-UX-001 6.3）。
/// </summary>
public static partial class GuardrailChecker
{
    [GeneratedRegex(@"([0-9０-９][0-9０-９,，]*)\s*円")]
    private static partial Regex YenRegex();

    /// <summary>マスター投稿（SNS 非依存）の検査。</summary>
    public static GuardrailReport CheckContent(string text, GuardrailContext ctx)
    {
        var findings = new List<GuardrailFinding>();
        CheckNgWords(text, ctx, findings);
        CheckRegulated(text, ctx, findings);
        CheckPrDisclosure(text, ctx, findings);
        CheckMustPhrases(text, ctx, findings);
        CheckPrices(text, ctx, findings);
        return new GuardrailReport(findings);
    }

    /// <summary>SNS 別バリアントの検査（内容＋プラットフォーム制約）。</summary>
    public static GuardrailReport CheckVariant(string body, IReadOnlyList<string> hashtags, PlatformConstraint platform,
        GuardrailContext ctx, string? title = null)
    {
        var composed = PostText.Compose(body, hashtags);
        return CheckContent(composed, ctx).Merge(CheckPlatform(composed, platform, title));
    }

    /// <summary>プラットフォーム制約（文字数・ハッシュタグ数・リンク）の検査。</summary>
    public static GuardrailReport CheckPlatform(string composedText, PlatformConstraint p, string? title = null)
    {
        var findings = new List<GuardrailFinding>();
        var length = PostText.Length(composedText);
        if (length > p.MaxBodyLength)
        {
            findings.Add(new(GuardrailLevel.Error, GuardrailCodes.LengthExceeded,
                $"{p.DisplayName}の文字数の上限（{p.MaxBodyLength:N0}字）を {length - p.MaxBodyLength:N0}字こえています。短くすると投稿できます。",
                Reason: $"{p.DisplayName}の仕様"));
        }

        if (title is not null && p.MaxTitleLength is { } maxTitle && PostText.Length(title) > maxTitle)
        {
            findings.Add(new(GuardrailLevel.Error, GuardrailCodes.LengthExceeded,
                $"{p.DisplayName}のタイトルは{maxTitle}字までです。短くすると投稿できます。", Reason: $"{p.DisplayName}の仕様"));
        }

        var tagCount = PostText.Hashtags(composedText).Count;
        if (p.MaxHashtags is { } maxTags && tagCount > maxTags)
        {
            findings.Add(new(GuardrailLevel.Error, GuardrailCodes.HashtagExceeded,
                maxTags == 0
                    ? $"{p.DisplayName}ではハッシュタグを使いません（現在 {tagCount}つ）。外すと投稿できます。"
                    : $"{p.DisplayName}のハッシュタグは{maxTags}つまでです（現在 {tagCount}つ）。",
                Reason: $"{p.DisplayName}の仕様"));
        }
        else if (tagCount < p.RecommendedHashtags.Min || tagCount > p.RecommendedHashtags.Max)
        {
            findings.Add(new(GuardrailLevel.Info, GuardrailCodes.HashtagRecommendation,
                $"{p.DisplayName}のハッシュタグは{FormatRange(p.RecommendedHashtags)}がおすすめです（現在 {tagCount}つ）。"));
        }

        var hasUrl = PostText.ContainsUrl(composedText);
        switch (p.LinkPolicy)
        {
            case LinkPolicy.DiscouragedByCost when hasUrl:
                findings.Add(new(GuardrailLevel.Info, GuardrailCodes.XUrlCost,
                    $"{p.DisplayName}でURLを含む投稿は1件あたりの費用が高くなります（推定 ${p.CostPerPostWithUrlUsd:0.000}、URLなしは ${p.CostPerPostUsd:0.000}）。リンクは返信に入れることをおすすめします。",
                    Reason: "X API 従量課金"));
                break;
            case LinkPolicy.NotClickable when hasUrl:
                findings.Add(new(GuardrailLevel.Info, GuardrailCodes.LinkNotClickable,
                    $"{p.DisplayName}の本文のリンクは押せません。「プロフィールのリンクから」と案内すると親切です。"));
                break;
            case LinkPolicy.NotAllowed when hasUrl:
                findings.Add(new(GuardrailLevel.Warning, GuardrailCodes.LinkNotAllowed,
                    $"{p.DisplayName}ではリンクを投稿に入れられません。URLを外してください。", Reason: $"{p.DisplayName}の仕様"));
                break;
            case LinkPolicy.Required when !hasUrl:
                findings.Add(new(GuardrailLevel.Error, GuardrailCodes.LinkRequired,
                    $"{p.DisplayName}では遷移先のURLが必要です。商品ページなどのURLを入れてください。", Reason: $"{p.DisplayName}の仕様"));
                break;
        }

        return new GuardrailReport(findings);
    }

    private static string FormatRange((int Min, int Max) r) =>
        r.Min == r.Max ? $"{r.Min}つ" : $"{r.Min}〜{r.Max}つ";

    private static void CheckNgWords(string text, GuardrailContext ctx, List<GuardrailFinding> findings)
    {
        foreach (var word in ctx.NgWords.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct())
        {
            var idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            findings.Add(new(GuardrailLevel.Error, GuardrailCodes.NgWord,
                $"「{word}」はブランドで使わない言葉に登録されています。別の言い方に書き換えてください。",
                Reason: "ブランド設定のNGワード", Excerpt: text.Substring(idx, word.Length), Index: idx));
        }
    }

    private static void CheckRegulated(string text, GuardrailContext ctx, List<GuardrailFinding> findings)
    {
        var covered = new List<(int Start, int End)>();
        void Scan(IEnumerable<(string Term, string Suggestion)> dict, string code, string law, string verb)
        {
            foreach (var (term, suggestion) in dict)
            {
                for (var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); idx >= 0;
                     idx = text.IndexOf(term, idx + term.Length, StringComparison.OrdinalIgnoreCase))
                {
                    var end = idx + term.Length;
                    if (covered.Any(c => idx < c.End && end > c.Start)) continue; // 長い語で検出済み
                    covered.Add((idx, end));
                    var excerpt = text.Substring(idx, term.Length);
                    findings.Add(new(GuardrailLevel.Warning, code,
                        $"「{excerpt}」{verb}（{law}）。「{suggestion}」に書き換えると安全です。",
                        Reason: law, Excerpt: excerpt, Index: idx, Suggestion: suggestion));
                }
            }
        }

        Scan(RegulatedExpressions.Premiums, GuardrailCodes.RegulatedExpression, "景品表示法", "と書くには根拠となる資料が必要です");
        if (RegulatedExpressions.IsPharmaIndustry(ctx.Industry))
        {
            Scan(RegulatedExpressions.Pharma, GuardrailCodes.PharmaExpression, "医薬品医療機器等法", "は効能効果の表現として認められない可能性があります");
        }
    }

    private static void CheckPrDisclosure(string text, GuardrailContext ctx, List<GuardrailFinding> findings)
    {
        if (!ctx.IsAdvertisement) return;
        if (RegulatedExpressions.PrDisclosures.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase))) return;
        // 広告の表記漏れは公開ブロック（RF-DES-001 9.2）
        findings.Add(new(GuardrailLevel.Error, GuardrailCodes.MissingPrDisclosure,
            "この投稿は広告のため「PR」の表記が必要です。先頭に「#PR」を入れると公開できます。",
            Reason: "景品表示法（ステルスマーケティング規制）", Suggestion: "#PR "));
    }

    private static void CheckMustPhrases(string text, GuardrailContext ctx, List<GuardrailFinding> findings)
    {
        foreach (var phrase in ctx.MustPhrases.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) continue;
            findings.Add(new(GuardrailLevel.Warning, GuardrailCodes.MustPhraseMissing,
                $"ブランドの必須表記「{phrase}」が入っていません。追加をおすすめします。", Reason: "ブランド設定の必須表記"));
        }
    }

    private static void CheckPrices(string text, GuardrailContext ctx, List<GuardrailFinding> findings)
    {
        if (ctx.KnownPrices.Count == 0) return;
        foreach (Match m in YenRegex().Matches(text))
        {
            var digits = NormalizeDigits(m.Groups[1].Value);
            if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;
            if (ctx.KnownPrices.Contains(value)) continue;
            // 割引額（例：50円引き）の可能性もあるため警告に留める
            findings.Add(new(GuardrailLevel.Warning, GuardrailCodes.PriceMismatch,
                $"「{m.Value}」が商品マスタの価格と一致しません。価格を確認してください。",
                Reason: "事実性チェック（商品マスタとの突合）", Excerpt: m.Value, Index: m.Index));
        }
    }

    private static string NormalizeDigits(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (c is >= '０' and <= '９') buf[n++] = (char)('0' + (c - '０'));
            else if (c is >= '0' and <= '9') buf[n++] = c;
        }
        return new string(buf[..n]);
    }
}
