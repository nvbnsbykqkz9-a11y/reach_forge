namespace ReachForge.Domain.Common;

/// <summary>
/// メッセージコード（RF-DES-001 15.2）。{レベル}-{領域}-{連番}。
/// 画面文言は UX ライティング規約（RF-UX-001 10章）に合わせ「原因＋解決策」で記述する。
/// </summary>
public static class ErrorCodes
{
    public const string SnsAuthCanceled = "E-SNS-001";
    public const string InstagramBusinessRequired = "E-SNS-002";
    public const string TikTokUnaudited = "W-SNS-003";
    public const string SnsReauthRequired = "E-SNS-010";
    public const string SnsDailyLimit = "E-SNS-020";
    public const string AiUnavailable = "E-AI-001";
    public const string AiSafetyBlocked = "E-AI-003";
    public const string AiRegulatedExpression = "W-AI-010";
    public const string AiMissingPrDisclosure = "W-AI-011";
    public const string PubXUrlCost = "W-PUB-001";
    public const string PubFailed = "E-PUB-010";
    public const string AprReapprovalRequired = "E-APR-001";
    public const string SysUnexpected = "E-SYS-500";
    public const string BrdUrlUnavailable = "W-BRD-001";

    // 本実装で追加したコード（設計書の体系に沿って採番）
    public const string AprInvalidTransition = "E-APR-002";
    public const string AprBlockedByGuardrail = "E-APR-003";
    public const string AprRejectReasonRequired = "E-APR-004";
    public const string PubNotApproved = "E-PUB-011";
    public const string PubScheduleInPast = "E-PUB-012";
    public const string PubPaused = "E-PUB-013";
    public const string BilPlanChannelLimit = "E-BIL-001";
    public const string SnsDuplicateAccount = "E-SNS-030";
    public const string NotFound = "E-SYS-404";
    public const string Validation = "E-SYS-400";
}

/// <summary>ガードレール指摘のコード（W-AI-010/011 以外は本実装での追加採番）。</summary>
public static class GuardrailCodes
{
    public const string RegulatedExpression = ErrorCodes.AiRegulatedExpression; // W-AI-010 景表法
    public const string MissingPrDisclosure = ErrorCodes.AiMissingPrDisclosure; // W-AI-011 ステマ規制
    public const string PharmaExpression = "W-AI-012";
    public const string LinkNotAllowed = "W-AI-013";
    public const string PriceMismatch = "W-AI-014";
    public const string MustPhraseMissing = "W-AI-015";
    public const string PromptInjection = "W-AI-016";
    public const string HashtagRecommendation = "I-AI-001";
    public const string LinkNotClickable = "I-AI-002";
    public const string XUrlCost = ErrorCodes.PubXUrlCost; // W-PUB-001
    public const string NgWord = "E-AI-020";
    public const string LengthExceeded = "E-AI-021";
    public const string HashtagExceeded = "E-AI-022";
    public const string LinkRequired = "E-AI-023";
}
