using ReachForge.Domain.Engagement;
using ReachForge.Domain.Entities;

namespace ReachForge.Domain.Tests;

public class EngagementTests
{
    [Fact]
    public void Masks_personal_information()
    {
        var masked = PiiMasker.Mask("山田です。090-1234-5678 か yamada@example.com に連絡ください。〒150-0001 @yamada_taro カード 4111 1111 1111 1111");
        Assert.DoesNotContain("090", masked);
        Assert.DoesNotContain("example.com", masked);
        Assert.DoesNotContain("150-0001", masked);
        Assert.DoesNotContain("yamada_taro", masked);
        Assert.DoesNotContain("4111", masked);
        Assert.Contains("[電話番号]", masked);
        Assert.Contains("[メール]", masked);
    }

    [Fact]
    public void Knowledge_search_finds_relevant_faq()
    {
        var faq = new[]
        {
            new KnowledgeEntry { Question = "営業時間は何時から何時までですか？", Answer = "平日8:00〜20:00、土日9:00〜18:00です。" },
            new KnowledgeEntry { Question = "駐車場はありますか？", Answer = "提携駐車場があります。" },
            new KnowledgeEntry { Question = "予約はできますか？", Answer = "席の予約はお電話で承ります。" },
        };
        var hits = KnowledgeMatcher.Search("土曜日は何時まで営業していますか", faq);
        Assert.Equal("営業時間は何時から何時までですか？", hits[0].Entry.Question);
        Assert.Empty(KnowledgeMatcher.Search("ラテアート最高でした！", faq));
    }

    private static InboxMessage Msg(DateTimeOffset at, Sentiment s, Guid? post = null) => new()
    {
        ExternalId = Guid.NewGuid().ToString(), Text = "x", ReceivedAt = at, Sentiment = s, PostVariantId = post,
    };

    [Fact]
    public void Flame_detected_when_negative_ratio_triples_or_post_spikes()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var normal = Enumerable.Range(1, 50).Select(i => Msg(now.AddHours(-i * 2), i % 10 == 0 ? Sentiment.Negative : Sentiment.Positive)).ToList();
        Assert.Null(FlameDetector.Detect(normal, now));

        var heated = normal.Concat(Enumerable.Range(1, 6).Select(i => Msg(now.AddMinutes(-i * 5), i <= 4 ? Sentiment.Negative : Sentiment.Neutral))).ToList();
        var signal = FlameDetector.Detect(heated, now);
        Assert.NotNull(signal);
        Assert.Null(signal.PostVariantId);

        var post = Guid.NewGuid();
        var spike = normal.Concat(Enumerable.Range(1, 5).Select(i => Msg(now.AddMinutes(-i), Sentiment.Negative, post))).ToList();
        Assert.Equal(post, FlameDetector.Detect(spike, now)!.PostVariantId);
    }
}
