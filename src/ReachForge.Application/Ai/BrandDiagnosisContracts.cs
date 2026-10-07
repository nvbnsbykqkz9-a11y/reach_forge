namespace ReachForge.Application.Ai;

/// <summary>取得した Web ページ（本文はタグを除いたテキスト）。<paramref name="Images"/> はページ内の画像（OGP 画像を先頭に）。</summary>
public sealed record WebPage(Uri Url, string Title, string Description, string Text, IReadOnlyList<string> Colors,
    IReadOnlyList<WebImage>? Images = null)
{
    public IReadOnlyList<WebImage> ImageList => Images ?? [];
}

/// <summary>ページ内の画像（代替テキスト付き）。<paramref name="IsShareImage"/> は og:image など SNS 共有用の画像。</summary>
public sealed record WebImage(Uri Url, string? Alt, bool IsShareImage = false);

/// <summary>取得した画像（JPEG / PNG / WebP）。</summary>
public sealed record FetchedImage(byte[] Bytes, string Mime);

/// <summary>Web ページの取得（SSRF 対策：公開 IP の http/https のみ、robots.txt を尊重、サイズ・時間の上限）。</summary>
public interface IWebPageFetcher
{
    /// <summary>取得できない（robots 拒否・404・非公開アドレスなど）場合は W-BRD-001 の DomainException。</summary>
    Task<WebPage> FetchAsync(string url, CancellationToken ct);

    /// <summary>ページ内の画像を取得する（同じ SSRF 対策・robots.txt。10MB まで）。取得できなければ W-BRD-001 の DomainException。</summary>
    Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct);
}

public sealed record PersonaDraft(string Name, string AgeRange, string Interests, string Pains);

public sealed record FaqDraft(string Question, string Answer);

/// <summary>ブランド診断の結果（RF-DES-001 F-02 BrandProfileDraft）。利用者が確認してから反映する。</summary>
public sealed record BrandProfileDraft(
    string BrandName,
    string Industry,
    int Casualness,
    string FirstPerson,
    int EmojiLevel,
    string? EndingRule,
    IReadOnlyList<PersonaDraft> Personas,
    IReadOnlyList<string> AppealPoints,
    IReadOnlyList<string> Hashtags,
    IReadOnlyList<string> NgWordSuggestions,
    IReadOnlyList<string> Colors,
    IReadOnlyList<FaqDraft> Faqs);

public sealed record BrandAnalysisInput(WebPage? Page, string? ExtraText, IReadOnlyList<string> PastPosts);

/// <summary>ブランド診断（LLM の構造化出力）。</summary>
public interface IBrandAnalyzer
{
    Task<BrandProfileDraft> AnalyzeAsync(BrandAnalysisInput input, CancellationToken ct);
}
