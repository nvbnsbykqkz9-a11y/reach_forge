using ReachForge.Application.Ai;

namespace ReachForge.AI.Prompts;

/// <summary>投稿文生成の構造化出力（配列はオブジェクトで包む）。</summary>
public sealed record CopyBatch(GeneratedCopy[] Candidates);

/// <summary>SNS 別バリアントの構造化出力。</summary>
public sealed record VariantDraft(string Body, string[] Hashtags, string? Title);

/// <summary>LLM-as-a-Judge のブランド適合度評価（1〜5）。</summary>
public sealed record JudgeResult(double Score, string Reason);

/// <summary>承認者向けの1行要約。</summary>
public sealed record DigestResult(string Message);

/// <summary>AI レポートの主張（evidence は根拠ファクトの ID：F1, F2 …）。</summary>
public sealed record ClaimDraft(string Text, string[] Evidence);

public sealed record InsightDraft(ClaimDraft[] Summary, ClaimDraft[] Good, ClaimDraft[] Issues, ClaimDraft[] NextActions);

/// <summary>受信メッセージの分類（列挙値は英語の識別子で受け取り、パースで検証する）。</summary>
public sealed record ClassificationDraft(string Sentiment, string Intent, string Urgency, string Sensitive, string Language);

public sealed record ReplyDraftItem(string Text, string[] Sources);

public sealed record ReplyBatch(ReplyDraftItem[] Replies);

public sealed record AbVariantDraft(string Body);

/// <summary>ブランド診断の構造化出力（RF-DES-001 F-02 BrandProfileDraft）。</summary>
public sealed record BrandDraftOutput(string BrandName, string Industry, int Casualness, string FirstPerson, int EmojiLevel,
    string? EndingRule, PersonaDraft[] Personas, string[] AppealPoints, string[] Hashtags, string[] NgWordSuggestions, FaqDraft[] Faqs);

public sealed record IdeaDraft(string Topic, double Relevance, string Format, string[] Angles, string Reason, bool Sensitive, int DaysBefore);

public sealed record IdeaBatch(IdeaDraft[] Ideas);
