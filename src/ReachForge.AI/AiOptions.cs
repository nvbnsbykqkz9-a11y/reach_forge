using ReachForge.Domain.Enums;

namespace ReachForge.AI;

/// <summary>
/// AI モデル設定（RF-DES-001 4.1 設定駆動）。モデル名・単価・フォールバック順は設定で管理し、
/// コードに特定モデルをハードコードしない。将来は DB の「AIモデル設定」マスタから読み込む。
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>プロバイダ名 → 設定。API キー未設定のプロバイダは使わない。</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = [];

    /// <summary>タスク種別 → プロバイダ名の優先順。"Default" はタスク未指定時に使う。</summary>
    public Dictionary<string, List<string>> Routes { get; set; } = [];

    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>生成1回あたりの出力トークン上限。</summary>
    public int MaxOutputTokens { get; set; } = 16000;

    /// <summary>「AI の利用料金（目安）」で円に換算するときの、1ドルあたりの円（目安）。</summary>
    public decimal UsdJpyRate { get; set; } = 150m;

    public IReadOnlyList<string> RouteFor(AiTaskType task) =>
        Routes.TryGetValue(task.ToString(), out var route) && route.Count > 0 ? route
        : Routes.TryGetValue("Default", out var fallback) ? fallback
        : [];
}

/// <summary>Google は動画生成（Veo、Gemini API）に使う。</summary>
public enum AiProviderType { Stub, OpenAI, Anthropic, Google }

public sealed class AiProviderOptions
{
    public AiProviderType Type { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>既定のモデル ID。</summary>
    public string? Model { get; set; }

    /// <summary>タスク別のモデル ID 上書き（例：Variant は低単価モデル）。</summary>
    public Dictionary<string, string> TaskModels { get; set; } = [];

    /// <summary>100万トークンあたりの単価（USD）。原価の記録に使う。</summary>
    public decimal InputPricePerMTok { get; set; }
    public decimal OutputPricePerMTok { get; set; }

    /// <summary>音声合成の声（OpenAI の voice 名など）。声のクローン（実在人物の声）は使わない。</summary>
    public string? Voice { get; set; }

    /// <summary>画像1枚あたりの単価（USD）。</summary>
    public decimal PricePerImage { get; set; }

    /// <summary>動画1秒あたりの単価（USD）。</summary>
    public decimal PricePerVideoSecond { get; set; }

    /// <summary>音声合成（ナレーション）1分あたりの単価（USD）。</summary>
    public decimal PricePerSpeechMinute { get; set; }

    /// <summary>API の接続先の上書き（検証環境・テスト用。通常は未設定）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>画像生成に対応するプロバイダか（Anthropic は画像の理解のみ）。</summary>
    public bool SupportsImageGeneration => Type is AiProviderType.OpenAI or AiProviderType.Stub;

    /// <summary>動画生成に対応するプロバイダか（OpenAI：Sora、Google：Veo）。</summary>
    public bool SupportsVideoGeneration => Type is AiProviderType.OpenAI or AiProviderType.Google or AiProviderType.Stub;

    public bool IsConfigured => Type == AiProviderType.Stub || !string.IsNullOrWhiteSpace(ApiKey);

    public string? ModelFor(AiTaskType task) => TaskModels.GetValueOrDefault(task.ToString(), Model ?? "");
}

/// <summary>サーキットブレーカー：5回連続失敗で60秒遮断（RF-DES-001 8.2）。</summary>
public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public int BreakSeconds { get; set; } = 60;
}
