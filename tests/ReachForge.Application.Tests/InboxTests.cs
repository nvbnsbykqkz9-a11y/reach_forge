using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Application.Services;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

public class InboxTests
{
    private static InboxItem Item(string id, string text, string? post = null, string author = "@guest") =>
        new(id, SocialPlatform.Instagram, InboxKind.Comment, author, author, text, DateTimeOffset.UnixEpoch, post);

    private static async Task<Channel> ChannelAsync(AppFixture f, SocialPlatform platform = SocialPlatform.Instagram)
    {
        await using var scope = f.Scope();
        return await f.Get<IAppDbContext>(scope).Channels.AsNoTracking().FirstAsync(c => c.Platform == platform);
    }

    private static async Task<int> IngestAsync(AppFixture f, Channel channel, params InboxItem[] items)
    {
        await using var worker = f.Scope(c => c.IsSystem = true);
        var now = f.Clock.GetUtcNow();
        return await f.Get<InboxService>(worker).IngestWebhookAsync(channel.Platform, channel.ExternalAccountId,
            items.Select(i => i with { ReceivedAt = i.ReceivedAt == DateTimeOffset.UnixEpoch ? now : i.ReceivedAt }).ToList(),
            CancellationToken.None);
    }

    [Fact]
    public async Task Ingests_once_classifies_and_sets_sla()
    {
        await using var f = await AppFixture.CreateAsync();
        var channel = await ChannelAsync(f);
        Assert.Equal(2, await IngestAsync(f, channel, Item("c1", "注文したのに届いていません。090-1111-2222 まで連絡ください"),
            Item("c2", "土曜日は何時まで営業していますか？")));
        Assert.Equal(0, await IngestAsync(f, channel, Item("c1", "注文したのに届いていません。"))); // 重複は取り込まない

        await using var scope = f.Scope();
        var inbox = f.Get<InboxService>(scope);
        var list = await inbox.ListAsync(new InboxQuery(), CancellationToken.None);
        var complaint = list.Single(m => m.ExternalId == "c1");
        Assert.Equal((InboxIntent.Complaint, Urgency.High, SensitiveTopic.Complaint), (complaint.Intent, complaint.Urgency, complaint.Sensitive));
        Assert.Equal(f.Clock.GetUtcNow().AddHours(1), complaint.DueAt);
        Assert.Equal(list.First(m => !m.IsOverdue(f.Clock.GetUtcNow())).Id, complaint.Id); // 期限切れの次に急ぎ
        var question = list.Single(m => m.ExternalId == "c2");
        Assert.Equal(InboxIntent.Question, question.Intent);

        // クレームは返信案を出さない
        await Assert.ThrowsAsync<DomainException>(() => inbox.SuggestRepliesAsync(complaint.Id, CancellationToken.None));

        var counts = await inbox.CountsAsync(CancellationToken.None);
        Assert.True(counts.Urgent >= 1 && counts.Complaint >= 1 && counts.Question >= 1);
    }

    [Fact]
    public async Task Suggests_replies_from_faq_then_replies_once()
    {
        await using var f = await AppFixture.CreateAsync();
        var channel = await ChannelAsync(f);
        await IngestAsync(f, channel, Item("q1", "土曜日は何時まで営業していますか？"));

        await using var scope = f.Scope(c => { c.Role = Role.Responder; c.UserName = "佐々木"; });
        var inbox = f.Get<InboxService>(scope);
        var id = (await inbox.ListAsync(new InboxQuery(InboxView.Question), CancellationToken.None)).Single(m => m.ExternalId == "q1").Id;
        var detail = await inbox.GetAsync(id, CancellationToken.None);
        Assert.Contains("営業時間", detail.Knowledge[0].Entry.Question);

        var before = (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance;
        var drafts = await inbox.SuggestRepliesAsync(id, CancellationToken.None);
        Assert.InRange(drafts.Count, 1, 3);
        Assert.Contains("9:00〜18:00", drafts[0].Text);
        Assert.Contains(drafts[0].Sources, s => s.Kind == "faq");
        Assert.Equal(before - 1, (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance);

        await inbox.TypingAsync(id, CancellationToken.None);
        await using (var other = f.Scope(c => { c.Role = Role.Owner; c.UserName = "田中"; }))
        {
            Assert.Equal("佐々木", (await f.Get<InboxService>(other).GetAsync(id, CancellationToken.None)).TypingOther);
        }

        await inbox.ReplyAsync(id, drafts[0].Text, CancellationToken.None);
        var replied = (await inbox.GetAsync(id, CancellationToken.None)).Message;
        Assert.Equal(InboxStatus.Replied, replied.Status);
        Assert.StartsWith("mock-reply-", replied.ReplyExternalId);
        await Assert.ThrowsAsync<DomainException>(() => inbox.ReplyAsync(id, "もう一度", CancellationToken.None));

        // 閲覧者は返信できない
        await using var viewer = f.Scope(c => c.Role = Role.Viewer);
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Get<InboxService>(viewer).ReplyAsync(id, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Automation_hides_spam_and_auto_replies_only_allowed_faq()
    {
        await using var f = await AppFixture.CreateAsync();
        await using (var scope = f.Scope())
        {
            await f.Get<InboxService>(scope).SaveSettingsAsync(new InboxSettings { AutoHideSpam = true, AutoReplyFaq = true }, CancellationToken.None);
        }
        var channel = await ChannelAsync(f);
        await IngestAsync(f, channel,
            Item("s1", "フォロワーを1000人増やします！DMください"),
            Item("f1", "テイクアウトはできますか？"),             // 自動返信を許可した FAQ
            Item("f2", "席の予約はできますか？"),                 // 許可していない FAQ → 人が対応
            Item("f3", "テイクアウトできないなんて最悪です"));     // 苦情 → 自動返信しない

        await using var check = f.Scope();
        var all = await f.Get<IAppDbContext>(check).InboxMessages.AsNoTracking().Where(m => m.ChannelId == channel.Id).ToListAsync();
        Assert.Equal(InboxStatus.Hidden, all.Single(m => m.ExternalId == "s1").Status);
        var auto = all.Single(m => m.ExternalId == "f1");
        Assert.Equal((InboxStatus.Replied, true), (auto.Status, auto.AutoHandled));
        Assert.Equal(InboxStatus.New, all.Single(m => m.ExternalId == "f2").Status);
        Assert.Equal(InboxStatus.New, all.Single(m => m.ExternalId == "f3").Status);
    }

    [Fact]
    public async Task Negative_spike_on_a_post_raises_alert_and_can_pause_publishing()
    {
        await using var f = await AppFixture.CreateAsync();
        var channel = await ChannelAsync(f);
        string postId;
        await using (var scope = f.Scope())
        {
            postId = (await f.Get<IAppDbContext>(scope).PostVariants
                .FirstAsync(v => v.ChannelId == channel.Id && v.ExternalPostId != null)).ExternalPostId!;
        }
        await IngestAsync(f, channel, Enumerable.Range(1, 5)
            .Select(i => Item($"n{i}", $"この投稿は不快です、がっかりしました {i}", postId, $"@user{i}")).ToArray());

        await using var owner = f.Scope();
        var inbox = f.Get<InboxService>(owner);
        var alert = Assert.Single(await inbox.OpenAlertsAsync(CancellationToken.None));
        Assert.NotNull(alert.PostVariantId);
        await inbox.ResolveAlertAsync(alert.Id, pausePublishing: true, CancellationToken.None);
        Assert.True(await f.Get<SchedulingService>(owner).IsPausedAsync(CancellationToken.None));
        Assert.Empty(await inbox.OpenAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Polling_imports_mock_comments_and_human_can_correct_labels()
    {
        await using var f = await AppFixture.CreateAsync();
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            var run = await f.Get<InboxService>(worker).PollDueAsync(CancellationToken.None);
            Assert.True(run.Channels > 0);
            Assert.Equal(0, run.Failed);
            // 間隔内の再実行では取りに行かない
            Assert.Equal(0, (await f.Get<InboxService>(worker).PollDueAsync(CancellationToken.None)).Channels);
        }

        await using var scope = f.Scope();
        var inbox = f.Get<InboxService>(scope);
        var message = (await inbox.ListAsync(new InboxQuery(), CancellationToken.None)).First(m => !m.RequiresHuman);
        await inbox.CorrectLabelsAsync(message.Id, Sentiment.Negative, InboxIntent.Complaint, Urgency.High, SensitiveTopic.Complaint,
            CancellationToken.None);
        var corrected = (await inbox.GetAsync(message.Id, CancellationToken.None)).Message;
        Assert.True(corrected.HumanCorrected);
        Assert.NotNull(corrected.AiLabels);
        Assert.Equal(InboxIntent.Complaint, corrected.Intent);
    }
}
