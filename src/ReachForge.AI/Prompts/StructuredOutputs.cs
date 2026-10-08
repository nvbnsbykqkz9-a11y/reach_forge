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

public sealed record SceneDraft(string Caption, string Narration, double Seconds);

public sealed record ScriptDraft(string Title, SceneDraft[] Scenes);

public sealed record LandingSceneDraft(string Role, string Caption, string Narration, double Seconds, int? ImageIndex, string[]? Points = null);

public sealed record LandingPlanDraft(string Title, string Product, string Target, string[] Benefits, string Offer, string CallToAction,
    LandingSceneDraft[] Scenes, string PostText, string[] Hashtags, string HookMotion);

public sealed record AdCopyDraft(string PrimaryText, string? Headline, string? Description, string? CallToAction);

public sealed record LpCreativeDraft(AdCopyDraft[] AdCopies, string? PostText, string[]? Hashtags);

/// <remarks><paramref name="Kind"/>：photo（写真）または screen（アプリ・Web サービスの画面）。</remarks>
public sealed record LpImagePickDraft(int Index, string? Description, string? Kind = null);

public sealed record LpImagePicksDraft(LpImagePickDraft[]? Picks);

public sealed record LpVisualDraft(int SourceIndex, string? Angle, string? Headline, string? ImagePrompt, string? MotionPrompt,
    string? BackdropPrompt = null);

/// <summary>LP から読み取った世界観（雰囲気・色・背景の模様・映像の舞台）。</summary>
public sealed record LpDirectionDraft(string? Mood, string[]? Palette, string? Motif, string? Setting);

public sealed record LpVisualsDraft(LpDirectionDraft? Direction, LpVisualDraft[]? Visuals);

/// <summary>広告写真の確認（点数 1〜5・合格か・問題点（日本語）・作り直しの指示（英語））。</summary>
public sealed record LpReviewDraft(int Score, bool Approved, string? Problems, string? Fix);
