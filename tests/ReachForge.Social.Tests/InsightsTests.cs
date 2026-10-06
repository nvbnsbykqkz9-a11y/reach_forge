using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Social.Insights;

namespace ReachForge.Social.Tests;

public class InsightsTests
{
    private static ChannelCredential Credential(SocialPlatform p) => new(Guid.NewGuid(), p, "acct", new StoredToken("access-token"));

    [Fact]
    public async Task X_reads_public_and_owner_metrics_in_one_call()
    {
        var http = new FakeHttp().Respond("2/tweets?ids=1%2C2", """
            {"data":[
              {"id":"1","public_metrics":{"like_count":10,"reply_count":2,"retweet_count":3,"quote_count":1,"bookmark_count":4,"impression_count":900},
               "non_public_metrics":{"impression_count":1000,"url_link_clicks":7,"user_profile_clicks":5}},
              {"id":"2","public_metrics":{"like_count":1,"impression_count":50}}
            ]}
            """);
        var result = await new XInsightsReader(http).GetPostMetricsAsync(["1", "2"], Credential(SocialPlatform.X), CancellationToken.None);

        Assert.Contains("non_public_metrics", Uri.UnescapeDataString(http.Requests[0].Uri.Query));
        var first = result.Single(r => r.ExternalPostId == "1");
        Assert.Equal(1000, first.Impressions);
        Assert.Equal(4, first.Shares);
        Assert.Equal(7, first.LinkClicks);
        Assert.Equal(50, result.Single(r => r.ExternalPostId == "2").Impressions); // 非公開指標がなければ公開値
    }

    [Fact]
    public async Task Instagram_maps_graph_insights()
    {
        var http = new FakeHttp().Respond("v24.0/m1/insights?metric=views", """
            {"data":[{"name":"views","values":[{"value":1500}]},{"name":"reach","values":[{"value":1200}]},
                     {"name":"likes","values":[{"value":80}]},{"name":"saved","values":[{"value":12}]},
                     {"name":"follows","total_value":{"value":3}}]}
            """);
        var r = Assert.Single(await new InstagramInsightsReader(http, FakeHttp.Options())
            .GetPostMetricsAsync(["m1"], Credential(SocialPlatform.Instagram), CancellationToken.None));
        Assert.Equal(1500, r.Impressions);
        Assert.Equal(1200, r.Reach);
        Assert.Equal(80, r.Likes);
        Assert.Equal(12, r.Saves);
        Assert.Equal(3, r.Follows);
    }

    [Fact]
    public async Task Line_reads_followers_for_previous_day()
    {
        var http = new FakeHttp().Respond("v2/bot/insight/followers?date=20261005", """{"status":"ready","followers":321}""");
        var r = await new LineInsightsReader(http).GetAccountMetricsAsync(new DateOnly(2026, 10, 6), Credential(SocialPlatform.Line),
            CancellationToken.None);
        Assert.Equal(321, r.Followers);
    }
}
