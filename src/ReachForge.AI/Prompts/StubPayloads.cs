using ReachForge.Domain.Enums;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;

namespace ReachForge.AI.Prompts;

// ローカル用スタブ（StubChatClient）が決定的な結果を返すための入力。実プロバイダには渡らない。
public sealed record CopyStubPayload(CopyRequest Request, BrandProfile Brand, IReadOnlyList<Product> Products,
    GeneratedCopy? Base = null, QuickFix? Fix = null);

public sealed record JudgeStubPayload(GeneratedCopy Copy, BrandProfile Brand);

public sealed record AltStubPayload(string Hint);

public sealed record BrandStubPayload(BrandAnalysisInput Input);

public sealed record ScriptStubPayload(string Theme, int SceneCount, int TargetSeconds, string BrandName);

public sealed record LandingPageStubPayload(WebPage Page, int SceneCount, int TargetSeconds, string BrandName);

public sealed record LpCreativeStubPayload(WebPage Page, SocialPlatform Platform, string BrandName);

public sealed record LpImagesStubPayload(IReadOnlyList<LpImageCandidate> Candidates, int Max);

public sealed record LpVisualsStubPayload(WebPage Page, IReadOnlyList<string> Sources, string BrandName);

/// <summary>広告写真の確認（スタブは常に合格）。</summary>
public sealed record LpReviewStubPayload(string Angle);
