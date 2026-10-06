using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>画像・動画・音声（RF-DES-001 6.2 (4)）。</summary>
public sealed class MediaAsset : Entity
{
    public const long MaxUploadBytes = 20 * 1024 * 1024;
    public static readonly IReadOnlyList<string> AcceptedUploadTypes = ["image/jpeg", "image/png", "image/webp"];

    public Guid WorkspaceId { get; set; }
    public MediaKind Kind { get; set; } = MediaKind.Image;
    public MediaSource Source { get; set; }
    public required string BlobPath { get; set; }
    public required string Mime { get; set; }
    public string FileName { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public long Bytes { get; set; }

    /// <summary>派生元（比率変換・編集元）。</summary>
    public Guid? ParentAssetId { get; set; }

    /// <summary>派生の種類（例：instagram:4x5:SmartCrop、thumb）。同じ派生は再利用する。</summary>
    public string? DerivationKey { get; set; }

    public Guid? AiGenerationId { get; set; }

    /// <summary>AI 生成物か（SNS の AI ラベル設定・画面の ✦ バッジに使う）。</summary>
    public bool IsAiLabeled { get; set; }

    /// <summary>来歴（モデル・プロンプトのハッシュ・生成日時など）の JSON。C2PA マニフェスト付与は今後対応。</summary>
    public string? Provenance { get; set; }

    public string? AltText { get; set; }
    public bool AltTextIsAi { get; set; }

    /// <summary>有害性判定の結果（"ok" / "not_checked" / ブロック分類）。</summary>
    public string SafetyResult { get; set; } = "not_checked";

    public List<string> Tags { get; set; } = [];

    public double AspectRatio => Width is > 0 && Height is > 0 ? (double)Width.Value / Height.Value : 0;
}

/// <summary>
/// 非同期の AI ジョブ（画像生成・編集など：RF-DES-001 14章 AiGenerationJob）。
/// 作成時に推定クレジットを予約し、成功時に実消費で確定、失敗・取り消し時は解放する。
/// </summary>
public sealed class AiJob : Entity
{
    public Guid WorkspaceId { get; set; }
    public AiTaskType TaskType { get; set; }
    public AiJobStatus Status { get; private set; } = AiJobStatus.Queued;
    public AiJobStage Stage { get; private set; } = AiJobStage.Waiting;

    /// <summary>要求内容の JSON（プロンプト・スタイル・枚数など）。</summary>
    public string RequestJson { get; set; } = "{}";
    public List<Guid> ResultAssetIds { get; private set; } = [];
    public int CreditsHeld { get; set; }
    public int CreditsCharged { get; private set; }
    public string RequestedBy { get; set; } = "";
    public string? ErrorCode { get; private set; }
    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is AiJobStatus.Succeeded or AiJobStatus.Failed or AiJobStatus.Canceled;

    public void Start(DateTimeOffset now)
    {
        if (Status != AiJobStatus.Queued) throw new DomainException(ErrorCodes.Validation, "このジョブは開始できません。");
        Status = AiJobStatus.Running;
        Stage = AiJobStage.Generating;
        StartedAt = now;
        Attempts++;
    }

    public void MoveTo(AiJobStage stage) => Stage = stage;

    public void Succeed(IEnumerable<Guid> assetIds, int creditsCharged, DateTimeOffset now)
    {
        ResultAssetIds = [.. assetIds];
        CreditsCharged = creditsCharged;
        Status = AiJobStatus.Succeeded;
        Stage = AiJobStage.Done;
        CompletedAt = now;
    }

    public void Fail(string errorCode, string error, DateTimeOffset now)
    {
        ErrorCode = errorCode;
        Error = error;
        Status = AiJobStatus.Failed;
        Stage = AiJobStage.Done;
        CompletedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != AiJobStatus.Queued) throw new DomainException(ErrorCodes.Validation, "開始済みのジョブは取り消せません。");
        Status = AiJobStatus.Canceled;
        Stage = AiJobStage.Done;
        CompletedAt = now;
    }

    /// <summary>実行中のまま止まったジョブ（プロセス停止など）を判定する。</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan timeout) => Status == AiJobStatus.Running && StartedAt is { } s && now - s > timeout;
}
