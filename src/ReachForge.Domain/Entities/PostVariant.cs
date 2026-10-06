using System.Security.Cryptography;
using System.Text;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.Domain.Entities;

/// <summary>
/// マスター投稿を特定チャネル向けに最適化した投稿単位（RF-DES-001 6.2 (3)）。
/// 状態遷移は図12-1 に従い、本クラスのメソッド経由でのみ変更する。
/// </summary>
public sealed class PostVariant : Entity
{
    public const int MaxPublishRetries = 5;

    public Guid MasterPostId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ChannelId { get; set; }
    public SocialPlatform Platform { get; set; }

    public string Body { get; private set; } = "";
    public List<string> Hashtags { get; private set; } = [];
    /// <summary>YouTube / Pinterest のタイトル。</summary>
    public string? Title { get; private set; }
    public List<Guid> MediaAssetIds { get; private set; } = [];

    /// <summary>SNS 固有設定（IG：Reels/カルーセル、TikTok：公開範囲 等）。</summary>
    public Dictionary<string, string> PlatformOptions { get; set; } = [];

    public VariantStatus Status { get; private set; } = VariantStatus.Draft;

    /// <summary>予約日時（UTC 保存、表示はテナント TZ）。</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>承認依頼時に指定した希望公開日時。承認されると自動で予約し、期限までに未承認なら保留にする（F-07-2）。</summary>
    public DateTimeOffset? RequestedPublishAt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? ExternalPostId { get; private set; }
    public string? Url { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastError { get; private set; }

    public string? AbGroup { get; set; }
    public Guid? AiGenerationId { get; set; }
    public bool IsAiEdited { get; private set; }

    /// <summary>X の URL 付き投稿の費用増を利用者が明示的に承知したか（F-06 業務ルール）。</summary>
    public bool UrlCostAcknowledged { get; set; }

    /// <summary>最新のガードレール結果。</summary>
    public List<GuardrailFinding> GuardrailFindings { get; private set; } = [];
    public GuardrailLevel GuardrailLevel { get; private set; }

    /// <summary>承認時点の内容ハッシュ。承認後の変更検知に使う。</summary>
    public string? ApprovedContentHash { get; private set; }

    /// <summary>直近の差戻し理由。</summary>
    public string? RejectReason { get; private set; }

    public string? HoldReason { get; private set; }

    public bool HasGuardrailErrors => GuardrailLevel == GuardrailLevel.Error;

    /// <summary>冪等キー（variant_id＋scheduled_at）。PublishJob の二重実行防止に使う（F-08）。</summary>
    public string IdempotencyKey => $"{Id:N}:{ScheduledAt?.UtcTicks}";

    public static PostVariant Create(MasterPost master, Channel channel, string body, IEnumerable<string> hashtags,
        string? title = null, Guid? aiGenerationId = null) =>
        new()
        {
            TenantId = master.TenantId,
            MasterPostId = master.Id,
            WorkspaceId = master.WorkspaceId,
            ChannelId = channel.Id,
            Platform = channel.Platform,
            Body = body,
            Hashtags = [.. hashtags],
            Title = title,
            AiGenerationId = aiGenerationId,
        };

    public void ApplyGuardrail(GuardrailReport report)
    {
        GuardrailFindings = [.. report.Findings];
        GuardrailLevel = report.Level;
    }

    /// <summary>
    /// 本文等を編集する。承認が必要なワークスペースで承認後に内容が変わった場合は Draft に戻し、
    /// 再承認を要する（E-APR-001）。戻り値は再承認が必要になったかどうか。
    /// </summary>
    public bool Edit(string body, IEnumerable<string> hashtags, string? title, bool requiresApproval)
    {
        EnsureNot([VariantStatus.Publishing, VariantStatus.Published, VariantStatus.Canceled], "編集");
        Body = body;
        Hashtags = [.. hashtags];
        Title = title;
        if (AiGenerationId is not null) IsAiEdited = true;

        if (Status == VariantStatus.Failed)
        {
            Status = VariantStatus.Draft;
            return false;
        }

        var approvedState = Status is VariantStatus.Approved or VariantStatus.Scheduled or VariantStatus.OnHold
                            && ApprovedContentHash is not null;
        if (requiresApproval && approvedState && ApprovedContentHash != ComputeContentHash())
        {
            Status = VariantStatus.Draft;
            ScheduledAt = null;
            NextAttemptAt = null;
            ApprovedContentHash = null;
            return true;
        }

        if (requiresApproval && Status == VariantStatus.InReview)
        {
            // 審査中の変更は申請し直し
            Status = VariantStatus.Draft;
        }
        return false;
    }

    public void SetMedia(IEnumerable<Guid> mediaAssetIds)
    {
        EnsureNot([VariantStatus.Publishing, VariantStatus.Published, VariantStatus.Canceled], "メディアの変更");
        MediaAssetIds = [.. mediaAssetIds];
    }

    /// <summary>承認を依頼する（Draft → InReview）。</summary>
    public void Submit(DateTimeOffset? requestedPublishAt = null)
    {
        EnsureIn([VariantStatus.Draft], "承認の依頼");
        if (HasGuardrailErrors)
        {
            throw new DomainException(ErrorCodes.AprBlockedByGuardrail,
                "確認結果にエラーがあるため承認を依頼できません。修正案を適用してからもう一度お試しください。");
        }
        RejectReason = null;
        RequestedPublishAt = requestedPublishAt?.ToUniversalTime();
        Status = VariantStatus.InReview;
    }

    /// <summary>
    /// 承認する（InReview → Approved）。ガードレールにエラーがある場合は Owner の理由付き例外承認のみ可（F-07-4）。
    /// </summary>
    public void Approve(bool asOwnerException = false, string? exceptionReason = null)
    {
        EnsureIn([VariantStatus.InReview], "承認");
        if (HasGuardrailErrors)
        {
            if (!asOwnerException)
            {
                throw new DomainException(ErrorCodes.AprBlockedByGuardrail,
                    "確認結果にエラーがあるため承認できません。Owner は理由を入力して例外として承認できます。");
            }
            if (string.IsNullOrWhiteSpace(exceptionReason))
            {
                throw new DomainException(ErrorCodes.AprRejectReasonRequired, "例外として承認する理由を入力してください。");
            }
        }
        Status = VariantStatus.Approved;
        ApprovedContentHash = ComputeContentHash();
    }

    /// <summary>差し戻す（InReview → Draft）。理由の入力は必須（RF-UX-001 SCR-08）。</summary>
    public void Reject(string reason)
    {
        EnsureIn([VariantStatus.InReview], "差し戻し");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(ErrorCodes.AprRejectReasonRequired, "差し戻す理由を入力してください。");
        }
        RejectReason = reason;
        Status = VariantStatus.Draft;
    }

    /// <summary>予約する。承認必須ワークスペースでは Approved 状態のみ予約可（11.1 権限マトリクス ※）。</summary>
    public void Schedule(DateTimeOffset scheduledAtUtc, DateTimeOffset now, bool requiresApproval)
    {
        EnsureNot([VariantStatus.Publishing, VariantStatus.Published, VariantStatus.Canceled], "予約");
        if (requiresApproval && Status is VariantStatus.Draft or VariantStatus.InReview)
        {
            throw new DomainException(ErrorCodes.PubNotApproved, "承認されていないため予約できません。先に承認を依頼してください。");
        }
        if (HasGuardrailErrors)
        {
            throw new DomainException(ErrorCodes.AprBlockedByGuardrail, "確認結果にエラーがあるため予約できません。修正してからもう一度お試しください。");
        }
        if (scheduledAtUtc <= now)
        {
            throw new DomainException(ErrorCodes.PubScheduleInPast, "過去の日時には予約できません。日時を選び直してください。");
        }
        if (!requiresApproval && Status == VariantStatus.Draft)
        {
            ApprovedContentHash = ComputeContentHash();
        }
        ScheduledAt = scheduledAtUtc.ToUniversalTime();
        RequestedPublishAt = ScheduledAt;
        NextAttemptAt = ScheduledAt;
        RetryCount = 0;
        HoldReason = null;
        Status = VariantStatus.Scheduled;
    }

    /// <summary>予約を取り消して予約前の状態に戻す。</summary>
    public void Unschedule(bool requiresApproval)
    {
        EnsureIn([VariantStatus.Scheduled, VariantStatus.OnHold], "予約の取り消し");
        ScheduledAt = null;
        NextAttemptAt = null;
        Status = requiresApproval ? VariantStatus.Approved : VariantStatus.Draft;
    }

    /// <summary>保留にする（承認期限切れ・チャネル切断・緊急停止・炎上時）。</summary>
    public void Hold(string reason)
    {
        EnsureIn([VariantStatus.Scheduled, VariantStatus.Approved, VariantStatus.InReview], "保留");
        HoldReason = reason;
        NextAttemptAt = null;
        Status = VariantStatus.OnHold;
    }

    public bool IsDue(DateTimeOffset now) =>
        Status == VariantStatus.Scheduled && NextAttemptAt is { } t && t <= now;

    public void MarkPublishing()
    {
        EnsureIn([VariantStatus.Scheduled], "公開処理の開始");
        Status = VariantStatus.Publishing;
    }

    public void MarkPublished(string externalPostId, string? url, DateTimeOffset now)
    {
        EnsureIn([VariantStatus.Publishing], "公開完了");
        ExternalPostId = externalPostId;
        Url = url;
        PublishedAt = now;
        NextAttemptAt = null;
        LastError = null;
        LastErrorCode = null;
        Status = VariantStatus.Published;
    }

    /// <summary>一時的なエラーで再試行を予定する（指数バックオフ、最大5回・上限30分）。</summary>
    public void ScheduleRetry(DateTimeOffset now, string errorCode, string error)
    {
        EnsureIn([VariantStatus.Publishing], "再試行");
        RetryCount++;
        LastErrorCode = errorCode;
        LastError = error;
        if (RetryCount > MaxPublishRetries)
        {
            Status = VariantStatus.Failed;
            NextAttemptAt = null;
            return;
        }
        var delay = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, RetryCount - 1)));
        NextAttemptAt = now + delay;
        Status = VariantStatus.Scheduled;
    }

    public void MarkFailed(string errorCode, string error)
    {
        EnsureIn([VariantStatus.Publishing], "失敗の記録");
        LastErrorCode = errorCode;
        LastError = error;
        NextAttemptAt = null;
        Status = VariantStatus.Failed;
    }

    /// <summary>失敗した投稿を利用者が手動で再実行する。</summary>
    public void RetryNow(DateTimeOffset now)
    {
        EnsureIn([VariantStatus.Failed], "再実行");
        RetryCount = 0;
        NextAttemptAt = now;
        ScheduledAt ??= now;
        Status = VariantStatus.Scheduled;
    }

    /// <summary>取り消す（Published 以外の各状態から可）。</summary>
    public void Cancel()
    {
        EnsureNot([VariantStatus.Published, VariantStatus.Canceled, VariantStatus.Publishing], "取り消し");
        NextAttemptAt = null;
        Status = VariantStatus.Canceled;
    }

    public string ComputeContentHash()
    {
        var raw = string.Join('\u001f', [Body, Title ?? "", string.Join(',', Hashtags), string.Join(',', MediaAssetIds)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private void EnsureIn(VariantStatus[] allowed, string action)
    {
        if (!allowed.Contains(Status)) throw InvalidTransition(action);
    }

    private void EnsureNot(VariantStatus[] disallowed, string action)
    {
        if (disallowed.Contains(Status)) throw InvalidTransition(action);
    }

    private DomainException InvalidTransition(string action) =>
        new(ErrorCodes.AprInvalidTransition, $"現在の状態（{Status.ToLabel()}）では{action}できません。");
}
