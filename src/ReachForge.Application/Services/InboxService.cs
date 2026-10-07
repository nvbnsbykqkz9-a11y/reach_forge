using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Engagement;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public enum InboxView { All, Urgent, Question, PurchaseOrReservation, Complaint, Spam, Overdue, Done }

public sealed record InboxQuery(InboxView View = InboxView.All, IReadOnlyCollection<SocialPlatform>? Platforms = null,
    bool MineOnly = false, bool UnassignedOnly = false, int Take = 200);

public sealed record InboxCounts(int All, int Urgent, int Question, int PurchaseOrReservation, int Complaint, int Spam, int Overdue);

public sealed record InboxDetail(InboxMessage Message, string ChannelName, string? OriginalPost, string? TypingOther,
    decimal? ReplyCostUsd, IReadOnlyList<KnowledgeHit> Knowledge);

public sealed record InboxRunResult(int Channels, int Ingested, int AutoHandled, int Failed);

/// <summary>
/// 統合受信箱（F-09）：取り込み（ポーリング・Webhook）→ 個人情報マスク → 分類 → SLA → 自動対応（許可したカテゴリのみ）
/// → 炎上の兆しの検知。返信案は FAQ・商品情報を参照（RAG）して最大3案、クレーム・医療・法務は人が対応する。
/// </summary>
public sealed class InboxService(
    IAppDbContext db,
    ITenantContext tenant,
    IInboxReaderFactory readers,
    ChannelTokenService tokens,
    IInboxClassifier classifier,
    IReplySuggester suggester,
    IBrandContextProvider brand,
    ICreditService credits,
    SchedulingService scheduling,
    TimeProvider clock,
    ILogger<InboxService> log)
{
    public const int MaxReplyLength = 1000;

    /// <summary>コメントを取りに行く投稿の範囲（公開後14日）。</summary>
    public static readonly TimeSpan RecentPostWindow = TimeSpan.FromDays(14);

    /// <summary>ポーリング間隔。X は読み取りが従量課金のため長めにする。</summary>
    /// <summary>取得間隔。X（従量課金）と YouTube（1日のクォータ）は長めにする。</summary>
    public static TimeSpan PollInterval(SocialPlatform p) =>
        p is SocialPlatform.X or SocialPlatform.YouTube ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);

    // ---------------- 取り込み ----------------

    /// <summary>InboxPollJob（14章：5〜15分ごと）。システムコンテキストで実行する。</summary>
    public async Task<InboxRunResult> PollDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("PollDueAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();
        var channels = (await db.Channels.Where(c => c.Status == ChannelStatus.Active).ToListAsync(ct))
            .Where(c => c.InboxSyncedAt is null || now - c.InboxSyncedAt >= PollInterval(c.Platform))
            .ToList();
        int count = 0, ingested = 0, auto = 0, failed = 0;
        foreach (var channel in channels)
        {
            if (readers.Get(channel.Platform, channel.IsDemo) is not { } reader) continue;
            try
            {
                var since = channel.InboxSyncedAt ?? now.AddDays(-2);
                var postIds = (await db.PostVariants
                        .Where(v => v.ChannelId == channel.Id && v.Status == VariantStatus.Published && v.ExternalPostId != null)
                        .Select(v => new { v.ExternalPostId, v.PublishedAt })
                        .ToListAsync(ct))
                    .Where(v => v.PublishedAt >= now - RecentPostWindow)
                    .OrderByDescending(v => v.PublishedAt)
                    .Select(v => v.ExternalPostId!)
                    .Take(20)
                    .ToList();
                var credential = await tokens.GetCredentialAsync(channel, ct);
                var items = await reader.FetchAsync(since, postIds, credential, ct);
                var (added, handled) = await IngestAsync(channel, items, ct);
                channel.InboxSyncedAt = now;
                await db.SaveChangesAsync(ct);
                ingested += added;
                auto += handled;
                count++;
            }
            catch (SocialApiException ex)
            {
                failed++;
                log.LogWarning("Inbox poll failed for channel {ChannelId} ({Platform}): {Code} {Message}",
                    channel.Id, channel.Platform, ex.ErrorCode, ex.Message);
                if (ex.RequiresReauth) await tokens.MarkReauthAsync(channel, ex.Message, ct);
            }
        }
        return new InboxRunResult(count, ingested, auto, failed);
    }

    /// <summary>Webhook で届いたコメント等を取り込む（SNS のアカウント ID からチャネルを特定する）。</summary>
    public async Task<int> IngestWebhookAsync(SocialPlatform platform, string externalAccountId, IReadOnlyList<InboxItem> items,
        CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("IngestWebhookAsync はシステムコンテキストで実行してください。");
        var channels = await db.Channels
            .Where(c => c.Platform == platform && c.ExternalAccountId == externalAccountId && c.Status != ChannelStatus.Revoked)
            .ToListAsync(ct);
        var total = 0;
        foreach (var channel in channels) total += (await IngestAsync(channel, items, ct)).Added;
        return total;
    }

    /// <summary>LINE の Webhook（チャネル ID が URL に含まれる）。</summary>
    public async Task<int> IngestForChannelAsync(Guid channelId, IReadOnlyList<InboxItem> items, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct) ?? throw new NotFoundException("チャネル");
        return (await IngestAsync(channel, items, ct)).Added;
    }

    private async Task<(int Added, int AutoHandled)> IngestAsync(Channel channel, IReadOnlyList<InboxItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return (0, 0);
        var ids = items.Select(i => i.ExternalId).Distinct().ToList();
        var existing = await db.InboxMessages.Where(m => m.ChannelId == channel.Id && ids.Contains(m.ExternalId))
            .Select(m => m.ExternalId).ToListAsync(ct);
        var fresh = items.Where(i => !existing.Contains(i.ExternalId) && i.AuthorId != channel.ExternalAccountId
                                     && !string.IsNullOrWhiteSpace(i.Text))
            .DistinctBy(i => i.ExternalId).ToList();
        if (fresh.Count == 0) return (0, 0);

        var postIds = fresh.Select(i => i.InReplyToExternalPostId).OfType<string>().Distinct().ToList();
        var variants = await db.PostVariants.Where(v => v.ChannelId == channel.Id && postIds.Contains(v.ExternalPostId!))
            .Select(v => new { v.Id, v.ExternalPostId }).ToListAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == channel.WorkspaceId, ct);
        var knowledge = await db.KnowledgeEntries.AsNoTracking().Where(k => k.WorkspaceId == channel.WorkspaceId).ToListAsync(ct);

        var autoHandled = 0;
        foreach (var item in fresh)
        {
            var message = new InboxMessage
            {
                TenantId = channel.TenantId,
                WorkspaceId = channel.WorkspaceId,
                ChannelId = channel.Id,
                Platform = channel.Platform,
                Kind = item.Kind,
                ExternalId = item.ExternalId,
                AuthorId = item.AuthorId,
                AuthorName = item.AuthorName,
                Text = item.Text.Length > 4000 ? item.Text[..4000] : item.Text,
                ReceivedAt = item.ReceivedAt,
                InReplyToExternalPostId = item.InReplyToExternalPostId,
                PostVariantId = variants.FirstOrDefault(v => v.ExternalPostId == item.InReplyToExternalPostId)?.Id,
            };
            // 個人情報をマスクしてから分類モデルへ送る（分類は無料）
            var labels = await classifier.ClassifyAsync(PiiMasker.Mask(message.Text), ct);
            message.Classify(labels.Sentiment, labels.Intent, labels.Urgency, labels.Sensitive, labels.Language,
                Sla(labels.Urgency, workspace.Inbox));
            db.InboxMessages.Add(message);
            if (await AutomateAsync(channel, message, item, workspace.Inbox, knowledge, ct)) autoHandled++;
        }
        await db.SaveChangesAsync(ct);
        await DetectFlameAsync(channel.TenantId, channel.WorkspaceId, ct);
        return (fresh.Count, autoHandled);
    }

    /// <summary>
    /// 管理者が許可したカテゴリだけ自動で対応する（スパムの非表示、許可した FAQ への自動返信）。
    /// クレーム・医療・法務は常に人が対応する。
    /// </summary>
    private async Task<bool> AutomateAsync(Channel channel, InboxMessage message, InboxItem item, InboxSettings settings,
        IReadOnlyList<KnowledgeEntry> knowledge, CancellationToken ct)
    {
        if (message.RequiresHuman || readers.Get(channel.Platform, channel.IsDemo) is not { } reader) return false;
        try
        {
            if (settings.AutoHideSpam && message.Intent == InboxIntent.Spam)
            {
                var credential = await tokens.GetCredentialAsync(channel, ct);
                if (await reader.HideAsync(item, credential, ct))
                {
                    message.Status = InboxStatus.Hidden;
                    message.AutoHandled = true;
                    return true;
                }
            }
            if (settings.AutoReplyFaq && message.Intent is InboxIntent.Question or InboxIntent.Purchase or InboxIntent.Reservation
                && message.Sentiment != Sentiment.Negative)
            {
                var hit = KnowledgeMatcher.Search(message.Text, knowledge.Where(k => k.AllowAutoReply), 1).FirstOrDefault();
                if (hit is { Score: >= KnowledgeMatcher.AutoReplyThreshold })
                {
                    var credential = await tokens.GetCredentialAsync(channel, ct);
                    var replyId = await reader.ReplyAsync(item, hit.Entry.Answer, credential, ct);
                    message.MarkReplied(hit.Entry.Answer, replyId, "自動返信", clock.GetUtcNow(), auto: true);
                    return true;
                }
            }
        }
        catch (SocialApiException ex)
        {
            log.LogWarning("Automatic inbox action failed for {ExternalId}: {Message}", message.ExternalId, ex.Message);
        }
        return false;
    }

    private static TimeSpan? Sla(Urgency urgency, InboxSettings settings) => urgency switch
    {
        Urgency.High => TimeSpan.FromHours(Math.Max(1, settings.SlaHighHours)),
        Urgency.Medium => TimeSpan.FromHours(Math.Max(1, settings.SlaMediumHours)),
        _ => null,
    };

    /// <summary>炎上の兆しを検知したらアラートを作る（同じワークスペースで1時間以内に未対応のアラートがあれば作らない）。</summary>
    private async Task DetectFlameAsync(Guid tenantId, Guid workspaceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var weekAgo = now.AddDays(-7);
        var messages = (await db.InboxMessages.AsNoTracking().Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct))
            .Where(m => m.ReceivedAt >= weekAgo).ToList();
        if (FlameDetector.Detect(messages, now) is not { } signal) return;
        var recentAlert = (await db.InboxAlerts.Where(a => a.WorkspaceId == workspaceId && a.Status == AlertStatus.Open).ToListAsync(ct))
            .Any(a => a.CreatedAt >= now.AddHours(-1));
        if (recentAlert) return;
        db.InboxAlerts.Add(new InboxAlert
        {
            TenantId = tenantId, WorkspaceId = workspaceId, Reason = signal.Reason, NegativeCount = signal.NegativeCount,
            RecentNegativeRatio = signal.RecentRatio, BaselineNegativeRatio = signal.BaselineRatio, PostVariantId = signal.PostVariantId,
        });
        await db.SaveChangesAsync(ct);
        log.LogWarning("Flame signal detected for workspace {WorkspaceId}: {Reason}", workspaceId, signal.Reason);
        // TODO(12章): 「炎上の兆し」通知（アプリ内・プッシュ・メール、オフ不可）
    }

    // ---------------- 一覧・詳細 ----------------

    public async Task<IReadOnlyList<InboxMessage>> ListAsync(InboxQuery query, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewInbox);
        var now = clock.GetUtcNow();
        var q = db.InboxMessages.AsNoTracking().Where(m => m.WorkspaceId == tenant.WorkspaceId);
        if (query.Platforms is { Count: > 0 } platforms) q = q.Where(m => platforms.Contains(m.Platform));
        if (query.MineOnly) q = q.Where(m => m.AssignedTo == tenant.UserName);
        if (query.UnassignedOnly) q = q.Where(m => m.AssignedTo == null);
        var list = await q.ToListAsync(ct);
        IEnumerable<InboxMessage> filtered = query.View switch
        {
            InboxView.Done => list.Where(m => !m.IsOpen && m.Status != InboxStatus.Hidden),
            InboxView.Spam => list.Where(m => m.Intent == InboxIntent.Spam),
            _ => list.Where(m => m.IsOpen && m.Intent != InboxIntent.Spam).Where(m => query.View switch
            {
                InboxView.Urgent => m.Urgency == Urgency.High,
                InboxView.Question => m.Intent == InboxIntent.Question,
                InboxView.PurchaseOrReservation => m.Intent is InboxIntent.Purchase or InboxIntent.Reservation,
                InboxView.Complaint => m.Intent == InboxIntent.Complaint || m.RequiresHuman,
                InboxView.Overdue => m.IsOverdue(now),
                _ => true,
            }),
        };
        // 期限切れ → 急ぎ → 新しい順
        return filtered
            .OrderByDescending(m => m.IsOverdue(now))
            .ThenBy(m => m.Urgency)
            .ThenByDescending(m => m.ReceivedAt)
            .Take(query.Take)
            .ToList();
    }

    public async Task<InboxCounts> CountsAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewInbox);
        var now = clock.GetUtcNow();
        var all = await db.InboxMessages.AsNoTracking()
            .Where(m => m.WorkspaceId == tenant.WorkspaceId && (m.Status == InboxStatus.New || m.Status == InboxStatus.InProgress))
            .ToListAsync(ct);
        var open = all.Where(m => m.Intent != InboxIntent.Spam).ToList();
        return new InboxCounts(open.Count, open.Count(m => m.Urgency == Urgency.High), open.Count(m => m.Intent == InboxIntent.Question),
            open.Count(m => m.Intent is InboxIntent.Purchase or InboxIntent.Reservation),
            open.Count(m => m.Intent == InboxIntent.Complaint || m.RequiresHuman), all.Count(m => m.Intent == InboxIntent.Spam),
            open.Count(m => m.IsOverdue(now)));
    }

    /// <summary>ナビの未対応件数（スパム以外）。閲覧権限がなければ 0。</summary>
    public async Task<int> OpenCountAsync(CancellationToken ct) =>
        RolePolicy.Can(tenant.Role, Permission.ViewInbox)
            ? await db.InboxMessages.CountAsync(m => m.WorkspaceId == tenant.WorkspaceId && m.Intent != InboxIntent.Spam
                                                     && (m.Status == InboxStatus.New || m.Status == InboxStatus.InProgress), ct)
            : 0;

    public async Task<InboxDetail> GetAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewInbox);
        var m = await Find(id, ct);
        await db.ReloadAsync(m, ct);
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == m.ChannelId, ct);
        string? post = null;
        if (m.PostVariantId is { } vid && await db.PostVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct) is { } v)
        {
            post = v.Body;
        }
        var knowledge = await db.KnowledgeEntries.AsNoTracking().Where(k => k.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct);
        var c = PlatformCatalog.Get(m.Platform);
        return new InboxDetail(m, channel?.DisplayName ?? c.DisplayName, post, m.TypingOther(tenant.UserName, clock.GetUtcNow()),
            m.Platform == SocialPlatform.X ? c.CostPerPostUsd : null, KnowledgeMatcher.Search(m.Text, knowledge));
    }

    // ---------------- 返信 ----------------

    /// <summary>返信案（最大3案・1クレジット）。クレーム・医療・法務は案を出さない。</summary>
    public async Task<IReadOnlyList<ReplyDraft>> SuggestRepliesAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        var m = await Find(id, ct);
        if (m.RequiresHuman)
        {
            throw new DomainException(ErrorCodes.Validation, "苦情・健康・法律に関わる内容のため、返信案は出しません。担当者が対応してください。");
        }
        var cost = CreditTable.Cost(CreditOperation.ReplySuggestion);
        await using var hold = await credits.HoldAsync(cost, ct);
        var ctx = await brand.BuildAsync(m.WorkspaceId, [], null, ct);
        var knowledge = await db.KnowledgeEntries.AsNoTracking().Where(k => k.WorkspaceId == m.WorkspaceId).ToListAsync(ct);
        var post = m.PostVariantId is { } vid ? (await db.PostVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct))?.Body : null;
        var drafts = await suggester.SuggestAsync(new ReplyRequest(ctx, PiiMasker.Mask(m.Text), post,
            KnowledgeMatcher.Search(m.Text, knowledge)), ct);
        if (drafts.Count == 0) throw new AiUnavailableException("返信案を作れませんでした。もう一度お試しください（クレジットは消費されていません）。");
        db.AiGenerations.Add(new AiGeneration
        {
            TenantId = tenant.TenantId, WorkspaceId = m.WorkspaceId, TaskType = AiTaskType.Reply, PromptKey = "inbox.reply", PromptVersion = 1,
            BrandProfileVersion = ctx.Profile.Version, Status = AiGenerationStatus.Succeeded,
            InputSnapshot = JsonSerializer.Serialize(new { inboxMessageId = m.Id }), Output = JsonSerializer.Serialize(drafts.Select(d => d.Text)),
            Credits = await hold.CommitAsync(cost, ct),
        });
        if (m.Status == InboxStatus.New) m.Status = InboxStatus.InProgress;
        await db.SaveChangesAsync(ct);
        return drafts;
    }

    /// <summary>返信を送る（案を押しただけでは送らない：入力欄に入れてから送信）。</summary>
    public async Task ReplyAsync(Guid id, string text, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        text = text.Trim();
        if (text.Length == 0) throw new DomainException(ErrorCodes.Validation, "返信を入力してください。");
        if (text.Length > MaxReplyLength) throw new DomainException(ErrorCodes.Validation, $"返信は{MaxReplyLength}字以内で入力してください。");
        var m = await Find(id, ct);
        await db.ReloadAsync(m, ct);
        if (m.Status == InboxStatus.Replied) throw new DomainException(ErrorCodes.Validation, $"{m.RepliedBy}さんが返信済みです。");
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == m.ChannelId, ct) ?? throw new NotFoundException("チャネル");
        if (channel.Status != ChannelStatus.Active)
        {
            throw new DomainException(ErrorCodes.SnsReauthRequired, $"{PlatformCatalog.Get(channel.Platform).DisplayName}の再接続が必要です。");
        }
        var reader = readers.Get(channel.Platform, channel.IsDemo)
                     ?? throw new DomainException(ErrorCodes.Validation, "このSNSには返信できません。");
        var credential = await tokens.GetCredentialAsync(channel, ct);
        try
        {
            var replyId = await reader.ReplyAsync(ToItem(m), text, credential, ct);
            m.MarkReplied(text, replyId, tenant.UserName, clock.GetUtcNow());
        }
        catch (SocialApiException ex)
        {
            if (ex.RequiresReauth) await tokens.MarkReauthAsync(channel, ex.Message, ct);
            throw new DomainException(ex.ErrorCode, $"返信できませんでした：{ex.Message}");
        }
        m.AssignedTo ??= tenant.UserName;
        db.Record(tenant, "inbox.replied", nameof(InboxMessage), m.Id, m.Platform.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task HideAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        var m = await Find(id, ct);
        var channel = await db.Channels.FirstAsync(c => c.Id == m.ChannelId, ct);
        var reader = readers.Get(channel.Platform, channel.IsDemo);
        if (reader is null || !await reader.HideAsync(ToItem(m), await tokens.GetCredentialAsync(channel, ct), ct))
        {
            throw new DomainException(ErrorCodes.Validation, $"{PlatformCatalog.Get(m.Platform).DisplayName}では非表示にできません。");
        }
        m.Status = InboxStatus.Hidden;
        db.Record(tenant, "inbox.hidden", nameof(InboxMessage), m.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(Guid id, InboxStatus status, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        if (status is not (InboxStatus.Closed or InboxStatus.New or InboxStatus.InProgress))
        {
            throw new DomainException(ErrorCodes.Validation, "この状態には変更できません。");
        }
        var m = await Find(id, ct);
        m.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task AssignAsync(Guid id, string? assignee, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        var m = await Find(id, ct);
        m.AssignedTo = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();
        if (m.Status == InboxStatus.New && m.AssignedTo is not null) m.Status = InboxStatus.InProgress;
        db.Record(tenant, "inbox.assigned", nameof(InboxMessage), m.Id, m.AssignedTo);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>入力中の表示（「佐々木さんが入力中」）。</summary>
    public async Task TypingAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        var m = await Find(id, ct);
        var now = clock.GetUtcNow();
        if (m.TypingBy == tenant.UserName && m.TypingAt is { } at && now - at < TimeSpan.FromSeconds(30)) return;
        m.TypingBy = tenant.UserName;
        m.TypingAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 他の人が同時に更新した（入力中の表示は厳密でなくてよい）
        }
    }

    /// <summary>AI の分類を人が直す（元の判定を残し、学習用に記録する）。</summary>
    public async Task CorrectLabelsAsync(Guid id, Sentiment sentiment, InboxIntent intent, Urgency urgency, SensitiveTopic sensitive,
        CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ReplyInbox);
        var m = await Find(id, ct);
        m.AiLabels ??= JsonSerializer.Serialize(new { m.Sentiment, m.Intent, m.Urgency, m.Sensitive });
        var settings = (await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == m.WorkspaceId, ct)).Inbox;
        m.Classify(sentiment, intent, urgency, sensitive, m.Language, Sla(urgency, settings));
        m.HumanCorrected = true;
        db.Record(tenant, "inbox.labels_corrected", nameof(InboxMessage), m.Id, $"{intent}/{sentiment}/{urgency}/{sensitive}");
        await db.SaveChangesAsync(ct);
    }

    // ---------------- 炎上アラート ----------------

    public async Task<IReadOnlyList<InboxAlert>> OpenAlertsAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewInbox);
        return (await db.InboxAlerts.AsNoTracking().Where(a => a.WorkspaceId == tenant.WorkspaceId && a.Status == AlertStatus.Open)
            .ToListAsync(ct)).OrderByDescending(a => a.CreatedAt).ToList();
    }

    /// <summary>アラートを閉じる。<paramref name="pausePublishing"/> なら予約投稿をすべて一時停止する（緊急停止）。</summary>
    public async Task ResolveAlertAsync(Guid id, bool pausePublishing, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, pausePublishing ? Permission.PauseAllPublishing : Permission.ReplyInbox);
        var alert = await db.InboxAlerts.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == tenant.WorkspaceId, ct)
                    ?? throw new NotFoundException("アラート");
        alert.Status = pausePublishing ? AlertStatus.Resolved : AlertStatus.Dismissed;
        alert.HandledBy = tenant.UserName;
        db.Record(tenant, pausePublishing ? "inbox.alert_paused" : "inbox.alert_dismissed", nameof(InboxAlert), alert.Id);
        await db.SaveChangesAsync(ct);
        if (pausePublishing) await scheduling.SetPausedAsync(true, ct);
    }

    // ---------------- FAQ（返信案の根拠）・設定 ----------------

    public async Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewInbox);
        return (await db.KnowledgeEntries.AsNoTracking().Where(k => k.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
            .OrderBy(k => k.CreatedAt).ToList();
    }

    public async Task<KnowledgeEntry> SaveKnowledgeAsync(Guid? id, string question, string answer, bool allowAutoReply, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        question = question.Trim();
        answer = answer.Trim();
        if (question.Length is 0 or > 200 || answer.Length is 0 or > 500)
        {
            throw new DomainException(ErrorCodes.Validation, "質問は200字以内、回答は500字以内で入力してください。");
        }
        KnowledgeEntry entry;
        if (id is { } existing)
        {
            entry = await db.KnowledgeEntries.FirstOrDefaultAsync(k => k.Id == existing && k.WorkspaceId == tenant.WorkspaceId, ct)
                    ?? throw new NotFoundException("FAQ");
            entry.Question = question;
            entry.Answer = answer;
        }
        else
        {
            entry = new KnowledgeEntry { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Question = question, Answer = answer };
            db.KnowledgeEntries.Add(entry);
        }
        entry.AllowAutoReply = allowAutoReply;
        db.Record(tenant, "knowledge.saved", nameof(KnowledgeEntry), entry.Id);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task DeleteKnowledgeAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        var entry = await db.KnowledgeEntries.FirstOrDefaultAsync(k => k.Id == id && k.WorkspaceId == tenant.WorkspaceId, ct)
                    ?? throw new NotFoundException("FAQ");
        db.KnowledgeEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<InboxSettings> SettingsAsync(CancellationToken ct) =>
        (await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == tenant.WorkspaceId, ct)).Inbox;

    public async Task SaveSettingsAsync(InboxSettings settings, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        var w = await db.Workspaces.FirstAsync(x => x.Id == tenant.WorkspaceId, ct);
        w.Inbox = new InboxSettings
        {
            AutoHideSpam = settings.AutoHideSpam, AutoReplyFaq = settings.AutoReplyFaq,
            SlaHighHours = Math.Clamp(settings.SlaHighHours, 1, 72), SlaMediumHours = Math.Clamp(settings.SlaMediumHours, 1, 168),
        };
        db.Record(tenant, "inbox.settings_updated", nameof(Workspace), w.Id);
        await db.SaveChangesAsync(ct);
    }

    private async Task<InboxMessage> Find(Guid id, CancellationToken ct) =>
        await db.InboxMessages.FirstOrDefaultAsync(m => m.Id == id && m.WorkspaceId == tenant.WorkspaceId, ct)
        ?? throw new NotFoundException("メッセージ");

    private static InboxItem ToItem(InboxMessage m) =>
        new(m.ExternalId, m.Platform, m.Kind, m.AuthorId, m.AuthorName, m.Text, m.ReceivedAt, m.InReplyToExternalPostId);
}
