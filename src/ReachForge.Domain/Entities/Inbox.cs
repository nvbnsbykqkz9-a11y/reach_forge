using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

public enum InboxKind : short { Comment = 1, Mention = 2, DirectMessage = 3 }

public enum Sentiment : short { Positive = 1, Neutral = 2, Negative = 3 }

/// <summary>意図（F-09 処理 2：質問／購入意向／予約／クレーム／称賛／スパム）。</summary>
public enum InboxIntent : short { Question = 1, Purchase = 2, Reservation = 3, Complaint = 4, Praise = 5, Spam = 6, Other = 7 }

public enum Urgency : short { High = 1, Medium = 2, Low = 3 }

/// <summary>人が対応すべき話題（クレーム・医療・法務）。返信案を出さず、自動返信もしない。</summary>
public enum SensitiveTopic : short { None = 0, Complaint = 1, Medical = 2, Legal = 3 }

public enum InboxStatus : short { New = 1, InProgress = 2, Replied = 3, Closed = 4, Hidden = 5 }

/// <summary>
/// 受信箱の1件（コメント・メンション・DM）。本文は原文で保存し、AI へ送るときだけ個人情報をマスクする（F-09 処理 1）。
/// </summary>
public sealed class InboxMessage : Entity
{
    public static readonly TimeSpan TypingWindow = TimeSpan.FromMinutes(2);

    public Guid WorkspaceId { get; set; }
    public Guid ChannelId { get; set; }
    public SocialPlatform Platform { get; set; }
    public InboxKind Kind { get; set; } = InboxKind.Comment;

    /// <summary>SNS 上の ID（チャネル内で一意）。重複取り込みの防止に使う。</summary>
    public required string ExternalId { get; set; }
    public string AuthorId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public required string Text { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>コメント先の投稿（SNS 上の ID と、自社の投稿バリアント）。</summary>
    public string? InReplyToExternalPostId { get; set; }
    public Guid? PostVariantId { get; set; }

    // ---- AI の分類（人が直せる。直した場合は HumanCorrected を立て、元の AI 判定を残す） ----
    public Sentiment Sentiment { get; set; } = Sentiment.Neutral;
    public InboxIntent Intent { get; set; } = InboxIntent.Other;
    public Urgency Urgency { get; set; } = Urgency.Low;
    public SensitiveTopic Sensitive { get; set; }
    public string Language { get; set; } = "ja";
    public bool IsClassified { get; set; }
    public bool HumanCorrected { get; set; }
    public string? AiLabels { get; set; }

    public InboxStatus Status { get; set; } = InboxStatus.New;
    public string? AssignedTo { get; set; }

    /// <summary>返信の期限（SLA：急ぎ 1時間、ふつう 24時間）。</summary>
    public DateTimeOffset? DueAt { get; set; }
    public string? ReplyText { get; set; }
    public string? ReplyExternalId { get; set; }
    public string? RepliedBy { get; set; }
    public DateTimeOffset? RepliedAt { get; set; }
    public bool AutoHandled { get; set; }

    /// <summary>返信を入力中の利用者（同時対応の防止。2分で失効）。</summary>
    public string? TypingBy { get; set; }
    public DateTimeOffset? TypingAt { get; set; }

    public bool RequiresHuman => Sensitive != SensitiveTopic.None;
    public bool IsOpen => Status is InboxStatus.New or InboxStatus.InProgress;
    public bool IsOverdue(DateTimeOffset now) => IsOpen && DueAt is { } d && d < now;

    public string? TypingOther(string me, DateTimeOffset now) =>
        TypingBy is { } who && who != me && TypingAt is { } at && now - at < TypingWindow ? who : null;

    public void Classify(Sentiment sentiment, InboxIntent intent, Urgency urgency, SensitiveTopic sensitive, string language,
        TimeSpan? sla)
    {
        Sentiment = sentiment;
        Intent = intent;
        Urgency = urgency;
        Sensitive = sensitive;
        Language = language;
        IsClassified = true;
        DueAt = sla is { } s ? ReceivedAt + s : null;
    }

    public void MarkReplied(string text, string? externalId, string by, DateTimeOffset now, bool auto = false)
    {
        if (Status == InboxStatus.Replied) throw new DomainException(ErrorCodes.Validation, "すでに返信済みです。");
        ReplyText = text;
        ReplyExternalId = externalId;
        RepliedBy = by;
        RepliedAt = now;
        AutoHandled = auto;
        Status = InboxStatus.Replied;
        TypingBy = null;
        TypingAt = null;
    }
}

/// <summary>返信案の根拠にする FAQ・営業時間・方針（F-09 処理 3 の RAG の知識）。</summary>
public sealed class KnowledgeEntry : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Question { get; set; }
    public required string Answer { get; set; }

    /// <summary>自動返信を許可するか（管理者が「定型質問」として許可したものだけ）。</summary>
    public bool AllowAutoReply { get; set; }
}

public enum AlertStatus : short { Open = 1, Dismissed = 2, Resolved = 3 }

/// <summary>炎上の兆し（否定的な反応の急増）の検知（F-09 処理 4）。</summary>
public sealed class InboxAlert : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Reason { get; set; }
    public int NegativeCount { get; set; }
    public double RecentNegativeRatio { get; set; }
    public double BaselineNegativeRatio { get; set; }
    public Guid? PostVariantId { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    public string? HandledBy { get; set; }
}

/// <summary>受信箱の自動対応の設定（管理者が許可したカテゴリのみ：F-09 処理 5）。</summary>
public sealed class InboxSettings
{
    /// <summary>スパムと判定したコメントを SNS 上で非表示にする。</summary>
    public bool AutoHideSpam { get; set; }

    /// <summary>自動返信を許可した FAQ に一致する質問へ、FAQ の回答で自動返信する。</summary>
    public bool AutoReplyFaq { get; set; }

    public int SlaHighHours { get; set; } = 1;
    public int SlaMediumHours { get; set; } = 24;
}
