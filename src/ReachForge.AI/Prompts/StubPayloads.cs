using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;

namespace ReachForge.AI.Prompts;

// ローカル用スタブ（StubChatClient）が決定的な結果を返すための入力。実プロバイダには渡らない。
public sealed record CopyStubPayload(CopyRequest Request, BrandProfile Brand, IReadOnlyList<Product> Products,
    GeneratedCopy? Base = null, QuickFix? Fix = null);

public sealed record VariantStubPayload(VariantRequest Request, string? Link, string? Feedback = null);

public sealed record JudgeStubPayload(GeneratedCopy Copy, BrandProfile Brand);

public sealed record DigestStubPayload(string Headline, string Body);

public sealed record AltStubPayload(string Hint);
