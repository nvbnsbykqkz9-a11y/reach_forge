using Microsoft.Extensions.Time.Testing;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Inbox;
using ReachForge.Social.Webhooks;

namespace ReachForge.Social.Tests;

public class InboxAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider Clock = new(Now);
    private static ChannelCredential Credential(SocialPlatform p) => new(Guid.NewGuid(), p, "acct", new StoredToken("access-token"));

    [Fact]
    public void Parses_meta_page_comments_instagram_comments_and_messages()
    {
        var page = WebhookParsers.ParseMeta("""
            {"object":"page","entry":[{"id":"page1","time":1,"changes":[
              {"field":"feed","value":{"item":"comment","verb":"add","comment_id":"c1","post_id":"p1","message":"何時まで？","from":{"id":"u1","name":"Hana"},"created_time":1790000000}},
              {"field":"feed","value":{"item":"reaction","verb":"add"}}],
             "messaging":[{"sender":{"id":"u2"},"recipient":{"id":"page1"},"timestamp":1790000000000,"message":{"mid":"m1","text":"予約したいです"}}]}]}
            """, Now);
        Assert.Equal(2, page.Count);
        Assert.Equal(("page1", "c1", "p1", InboxKind.Comment), (page[0].AccountId, page[0].Item.ExternalId, page[0].Item.InReplyToExternalPostId, page[0].Item.Kind));
        Assert.Equal(InboxKind.DirectMessage, page[1].Item.Kind);

        var ig = WebhookParsers.ParseMeta("""
            {"object":"instagram","entry":[{"id":"ig1","changes":[{"field":"comments","value":{"id":"ic1","text":"かわいい！","from":{"id":"x","username":"yuki"},"media":{"id":"mm1"}}}]}]}
            """, Now);
        var e = Assert.Single(ig);
        Assert.Equal((SocialPlatform.Instagram, "ig1", "@yuki", "mm1"), (e.Platform, e.AccountId, e.Item.AuthorName, e.Item.InReplyToExternalPostId));

        Assert.Empty(WebhookParsers.ParseMeta("not json", Now));
    }

    [Fact]
    public void Parses_line_text_messages_only()
    {
        var items = WebhookParsers.ParseLine("""
            {"destination":"x","events":[
              {"type":"message","message":{"type":"text","id":"lm1","text":"テイクアウトできますか"},"timestamp":1790000000000,"source":{"type":"user","userId":"U1"}},
              {"type":"message","message":{"type":"sticker","id":"lm2"},"source":{"type":"user","userId":"U1"}},
              {"type":"follow","source":{"type":"user","userId":"U2"}}]}
            """, Now);
        var item = Assert.Single(items);
        Assert.Equal(("lm1", "U1", InboxKind.DirectMessage), (item.ExternalId, item.AuthorId, item.Kind));
    }

    [Fact]
    public async Task Instagram_fetches_comments_since_and_replies()
    {
        var http = new FakeHttp()
            .Respond("v24.0/m1/comments", """
                {"data":[{"id":"c-new","text":"何時からですか？","username":"hana","timestamp":"2026-10-05T23:00:00+0000"},
                         {"id":"c-old","text":"古い","username":"old","timestamp":"2026-09-01T00:00:00+0000"}]}
                """)
            .Respond("v24.0/c-new/replies", """{"id":"r1"}""");
        var reader = new InstagramInboxReader(http, FakeHttp.Options(), Clock);
        var items = await reader.FetchAsync(Now.AddDays(-1), ["m1"], Credential(SocialPlatform.Instagram), CancellationToken.None);
        var item = Assert.Single(items);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero), item.ReceivedAt);

        Assert.Equal("r1", await reader.ReplyAsync(item, "9時からです", Credential(SocialPlatform.Instagram), CancellationToken.None));
        Assert.Contains("message=9", Uri.UnescapeDataString(http.Requests[1].Body!));
    }

    [Fact]
    public async Task X_reply_targets_tweet_and_line_reply_uses_push_with_retry_key()
    {
        var http = new FakeHttp().Respond("2/tweets", """{"data":{"id":"t9"}}""");
        var target = new InboxItem("t1", SocialPlatform.X, InboxKind.Mention, "u1", "@hana", "質問です", Now, "p1");
        Assert.Equal("t9", await new XInboxReader(http, Clock).ReplyAsync(target, "ありがとうございます", Credential(SocialPlatform.X), CancellationToken.None));
        Assert.Contains("\"in_reply_to_tweet_id\":\"t1\"", http.Requests[0].Body);

        var line = new FakeHttp().Respond("v2/bot/message/push", """{"sentMessages":[{"id":"s1"}]}""");
        var dm = new InboxItem("lm1", SocialPlatform.Line, InboxKind.DirectMessage, "U1", "LINE", "予約", Now, null);
        await new LineInboxReader(line).ReplyAsync(dm, "お電話で承ります", Credential(SocialPlatform.Line), CancellationToken.None);
        Assert.Contains("\"to\":\"U1\"", line.Requests[0].Body);
        Assert.True(line.Requests[0].Headers.ContainsKey("X-Line-Retry-Key"));
    }
}
