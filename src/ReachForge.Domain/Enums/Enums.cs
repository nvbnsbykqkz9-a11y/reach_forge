namespace ReachForge.Domain.Enums;

/// <summary>対象SNS（channel.platform の値は RF-DES-001 6.2 に合わせる）。</summary>
public enum SocialPlatform : short
{
    X = 1,
    Instagram = 2,
    Facebook = 3,
    Threads = 4,
    TikTok = 5,
    YouTube = 6,
    LinkedIn = 7,
    Line = 8,
    Pinterest = 9,
}

public enum ChannelStatus : short { Active = 1, ReauthRequired = 2, Revoked = 3, Error = 4 }

/// <summary>投稿の目的。</summary>
public enum PostObjective : short { Awareness = 1, Engagement = 2, Traffic = 3, Conversion = 4 }

/// <summary>投稿バリアントの状態（RF-DES-001 図12-1）。</summary>
public enum VariantStatus : short
{
    Draft = 1,
    InReview = 2,
    Approved = 3,
    Scheduled = 4,
    Publishing = 5,
    Published = 6,
    Failed = 7,
    Canceled = 8,
    /// <summary>保留（承認期限切れ・チャネル切断・緊急停止時）。</summary>
    OnHold = 9,
}

public enum MediaKind : short { Image = 1, Video = 2, Audio = 3 }

public enum MediaSource : short { Upload = 1, AiGenerated = 2, AiEdited = 3, Derived = 4 }

public enum AiTaskType : short
{
    Ideation = 1,
    Copy = 2,
    Variant = 3,
    Image = 4,
    ImageEdit = 5,
    Video = 6,
    Tts = 7,
    Reply = 8,
    Report = 9,
    Classify = 10,
    Judge = 11,
    Summarize = 12,
    /// <summary>画像・動画の理解（ALT テキスト生成・素材解析）。</summary>
    Vision = 13,

    /// <summary>生成 AI による動画（テキスト→動画・画像→動画）。テンプレート合成（Video）とは別のルート。</summary>
    VideoGeneration = 14,
}

public enum AiJobStatus : short { Queued = 1, Running = 2, Succeeded = 3, Failed = 4, Canceled = 5 }

/// <summary>非同期処理の段階（RF-UX-001 RfJobProgress「待機中 → 生成中 → 確認中 → 完了」）。</summary>
public enum AiJobStage : short { Waiting = 1, Generating = 2, Checking = 3, Done = 4 }

/// <summary>比率変換の方法（F-04-6：引き伸ばし禁止）。</summary>
public enum AspectMethod : short
{
    /// <summary>被写体を中心にしたスマートクロップ（既定）。</summary>
    SmartCrop = 1,
    /// <summary>余白（背景色）を付ける。</summary>
    Pad = 2,
    /// <summary>AI で画像を広げる（アウトペインティング）。</summary>
    Outpaint = 3,
}

/// <summary>画像のスタイル（F-04 入力）。</summary>
public enum ImageStyle : short { Photo = 1, Illustration = 2, Flat = 3, ThreeD = 4 }

public enum AiGenerationStatus : short { Queued = 1, Running = 2, Succeeded = 3, Failed = 4, Blocked = 5 }

/// <summary>ロール（RF-DES-001 9.1）。</summary>
public enum Role : short { Owner = 1, Admin = 2, Editor = 3, Approver = 4, Responder = 5, Viewer = 6 }

public enum GuardrailLevel : short { Ok = 0, Info = 1, Warning = 2, Error = 3 }

public enum ApprovalDecision : short { Submitted = 1, Approved = 2, Rejected = 3, Commented = 4, ExceptionApproved = 5 }

/// <summary>コピーライティングの型（F-03）。</summary>
public enum CopyFramework : short { Auto = 0, Aida = 1, Pas = 2, Story = 3, HowTo = 4, List = 5 }

