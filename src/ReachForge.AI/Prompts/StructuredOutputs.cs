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
