using ReachForge.Domain.Analytics;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Domain.Tests;

public class PostTextTests
{
    [Fact]
    public void Counts_emoji_as_single_characters() => Assert.Equal(3, PostText.Length("秋🍠☕"));

    [Fact]
    public void Extracts_hashtags_including_full_width_marker() =>
        Assert.Equal(["秋限定", "さつまいもラテ"], PostText.Hashtags("新作 #秋限定 ＃さつまいもラテ"));

    [Fact]
    public void Compose_does_not_duplicate_existing_tags() =>
        Assert.Equal("本文 #秋\n\n#ラテ", PostText.Compose("本文 #秋", ["#秋", "ラテ"]));

    [Fact]
    public void Truncate_adds_ellipsis_within_limit()
    {
        var t = PostText.Truncate("あいうえおかきくけこ", 5);
        Assert.Equal(5, PostText.Length(t));
        Assert.EndsWith("…", t);
    }
}

public class CreditAccountTests
{
    [Fact]
    public void Hold_commit_release_flow()
    {
        var a = CreditAccount.Open(Guid.NewGuid(), 100, new DateOnly(2026, 10, 1));
        a.Hold(10);
        Assert.Equal(90, a.Available);

        Assert.Equal(8, a.Commit(10, 8));
        Assert.Equal(92, a.Balance);
        Assert.Equal(0, a.Held);

        a.Hold(5);
        a.Release(5);
        Assert.Equal(92, a.Available);
    }

    [Fact]
    public void Insufficient_credits_raise_E_AI_002()
    {
        var a = CreditAccount.Open(Guid.NewGuid(), 3, new DateOnly(2026, 10, 1));
        var ex = Assert.Throws<DomainException>(() => a.Hold(5));
        Assert.Equal(ErrorCodes.AiInsufficientCredits, ex.ErrorCode);
    }

    [Fact]
    public void Low_threshold_is_twenty_percent()
    {
        var a = CreditAccount.Open(Guid.NewGuid(), 100, new DateOnly(2026, 10, 1));
        a.Hold(80);
        a.Commit(80, 80);
        Assert.True(a.IsLow);
        Assert.False(a.IsExhausted);
    }

    [Fact]
    public void Projects_exhaustion_date_from_pace()
    {
        var a = CreditAccount.Open(Guid.NewGuid(), 100, new DateOnly(2026, 10, 1));
        a.Hold(50);
        a.Commit(50, 50); // 5日で50消費 → 1日10
        Assert.Equal(new DateOnly(2026, 10, 10), a.ProjectedExhaustionDate(new DateOnly(2026, 10, 5)));
    }
}

public class AnalyticsTests
{
    [Fact]
    public void Normal_cdf_matches_known_values()
    {
        Assert.Equal(0.975, AbTestEvaluator.NormalCdf(1.96), 3);
        Assert.Equal(0.5, AbTestEvaluator.NormalCdf(0), 6);
    }

    [Fact]
    public void Ab_test_is_pending_with_small_samples() =>
        Assert.Equal(AbVerdict.Pending, AbTestEvaluator.Evaluate(100, 5, 100, 9).Verdict);

    [Fact]
    public void Ab_test_detects_significant_winner()
    {
        var r = AbTestEvaluator.Evaluate(10_000, 400, 10_000, 560);
        Assert.Equal(AbVerdict.BWins, r.Verdict);
        Assert.Contains("1.4倍", r.Summary);
    }

    [Fact]
    public void Ab_test_reports_no_difference_when_close() =>
        Assert.Equal(AbVerdict.NoDifference, AbTestEvaluator.Evaluate(5_000, 200, 5_000, 205).Verdict);

    [Fact]
    public void Engagement_rate_falls_back_to_reach()
    {
        var m = new PostMetric { Reach = 200, Likes = 10, Comments = 5, Shares = 3, Saves = 2 };
        Assert.Equal(0.1, MetricsCalculator.EngagementRate(m));
        Assert.Null(MetricsCalculator.EngagementRate(new PostMetric()));
    }

    [Fact]
    public void Best_time_uses_defaults_below_thirty_samples()
    {
        var slots = BestTimeCalculator.Suggest([], TimeZoneInfo.Utc, DateTimeOffset.UtcNow);
        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(0, s.SampleSize));
    }

    [Fact]
    public void Best_time_ranks_strongest_slot_first()
    {
        var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var metrics = Enumerable.Range(0, 40).Select(i =>
        {
            var strong = i % 2 == 0;
            var posted = now.AddDays(-(i + 1)).Date.AddHours(strong ? 19 : 9);
            return new PostMetric
            {
                PostedAt = new DateTimeOffset(posted, TimeSpan.Zero),
                Impressions = 1000,
                Likes = strong ? 80 : 30,
            };
        });

        var top = BestTimeCalculator.Suggest(metrics, TimeZoneInfo.Utc, now)[0];
        Assert.Equal(19, top.Hour);
    }

    [Fact]
    public void Next_occurrence_is_in_the_future_in_tenant_timezone()
    {
        var jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var now = new DateTimeOffset(2026, 10, 8, 11, 0, 0, TimeSpan.Zero); // 木曜 20:00 JST
        var next = BestTimeCalculator.NextOccurrence(new BestTimeSlot(DayOfWeek.Thursday, 19, 0, 0, ""), jst, now);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Utm_parameters_are_appended_once()
    {
        var url = UtmBuilder.Append("https://example.com/menu?id=1", SocialPlatform.Instagram, "autumn2026");
        Assert.Equal("https://example.com/menu?id=1&utm_source=instagram&utm_medium=social&utm_campaign=autumn2026", url);
        Assert.Equal(url, UtmBuilder.Append(url, SocialPlatform.X, "other"));
    }
}

public class PlatformCatalogTests
{
    [Fact]
    public void Catalog_covers_all_nine_platforms_with_initial_release_of_five()
    {
        Assert.Equal(9, PlatformCatalog.All.Count());
        Assert.Equal(
            [SocialPlatform.X, SocialPlatform.Instagram, SocialPlatform.Facebook, SocialPlatform.Threads, SocialPlatform.Line],
            PlatformCatalog.InitialRelease.Select(c => c.Platform));
    }
}
