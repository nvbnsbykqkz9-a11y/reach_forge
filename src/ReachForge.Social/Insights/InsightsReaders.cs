using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Social.Line;
using ReachForge.Social.Meta;
using ReachForge.Social.Threads;
using ReachForge.Social.X;

namespace ReachForge.Social.Insights;

/// <summary>
/// 各社の指標 API（F-10）。指標名は各社の変更に追随できるよう設定値にする（SocialOptions）。
/// 本番接続前に各社の公式ドキュメントで指標名・必要な権限を再確認すること。
/// </summary>
internal static class InsightsJson
{
    public static long Long(JsonNode? node, string path) =>
        long.TryParse(SocialHttp.StrOrNull(node, path), out var v) ? v : 0;

    public static int Int(JsonNode? node, string path) => (int)Math.Min(int.MaxValue, Long(node, path));

    /// <summary>Graph API の insights 応答（data[].name / data[].values[0].value または total_value.value）を辞書にする。</summary>
    public static Dictionary<string, long> GraphInsights(JsonNode? json)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (json?["data"] is not JsonArray data) return result;
        foreach (var item in data)
        {
            var name = SocialHttp.StrOrNull(item, "name");
            if (name is null) continue;
            var value = item?["values"] is JsonArray { Count: > 0 } values
                ? values[^1]?["value"]
                : item?["total_value"]?["value"];
            if (value is JsonValue v && long.TryParse(v.ToString(), out var n)) result[name] = n;
        }
        return result;
    }
}

/// <summary>
/// X：GET /2/tweets?ids=…&amp;tweet.fields=public_metrics,non_public_metrics（投稿者本人のトークンで公開後30日以内）。
/// フォロワー数は GET /2/users/me?user.fields=public_metrics。
/// </summary>
public sealed class XInsightsReader(IHttpClientFactory http) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.X;
    public bool IsSimulation => false;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(XConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var chunk in externalPostIds.Chunk(100))
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"2/tweets?{SocialHttp.Query(("ids", string.Join(',', chunk)), ("tweet.fields", "public_metrics,non_public_metrics"))}",
                credential.AccessToken), "X", ct);
            if (json["data"] is not JsonArray data) continue;
            foreach (var t in data)
            {
                var id = SocialHttp.StrOrNull(t, "id");
                if (id is null) continue;
                var impressions = InsightsJson.Long(t, "non_public_metrics.impression_count");
                if (impressions == 0) impressions = InsightsJson.Long(t, "public_metrics.impression_count");
                result.Add(new PostMetricSnapshot(id,
                    Impressions: impressions,
                    Reach: 0,
                    Views: 0,
                    Likes: InsightsJson.Int(t, "public_metrics.like_count"),
                    Comments: InsightsJson.Int(t, "public_metrics.reply_count"),
                    Shares: InsightsJson.Int(t, "public_metrics.retweet_count") + InsightsJson.Int(t, "public_metrics.quote_count"),
                    Saves: InsightsJson.Int(t, "public_metrics.bookmark_count"),
                    LinkClicks: InsightsJson.Int(t, "non_public_metrics.url_link_clicks"),
                    ProfileVisits: InsightsJson.Int(t, "non_public_metrics.user_profile_clicks")));
            }
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName),
            SocialHttp.Get("2/users/me?user.fields=public_metrics", credential.AccessToken), "X", ct);
        return new AccountMetricSnapshot(InsightsJson.Long(json, "data.public_metrics.followers_count"), 0, 0);
    }
}

/// <summary>
/// Facebook ページ：反応数・コメント数・シェア数は投稿のフィールド、表示回数・クリックは /{post-id}/insights（read_insights 権限）。
/// </summary>
public sealed class FacebookInsightsReader(IHttpClientFactory http, IOptions<SocialOptions> options) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.Facebook;
    public bool IsSimulation => false;
    private MetaOptions O => options.Value.Meta;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var id in externalPostIds)
        {
            var post = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{O.GraphVersion}/{Uri.EscapeDataString(id)}?fields=reactions.summary(total_count).limit(0),comments.summary(total_count).limit(0),shares",
                credential.AccessToken), "Facebook", ct);
            var insights = InsightsJson.GraphInsights(await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{O.GraphVersion}/{Uri.EscapeDataString(id)}/insights?{SocialHttp.Query(("metric", O.FacebookPostMetrics))}",
                credential.AccessToken), "Facebook", ct));
            result.Add(new PostMetricSnapshot(id,
                Impressions: Pick(insights, "post_impressions", "post_media_view", "views"),
                Reach: Pick(insights, "post_impressions_unique", "post_total_media_view_unique"),
                Views: 0,
                Likes: InsightsJson.Int(post, "reactions.summary.total_count"),
                Comments: InsightsJson.Int(post, "comments.summary.total_count"),
                Shares: InsightsJson.Int(post, "shares.count"),
                Saves: 0,
                LinkClicks: (int)Pick(insights, "post_clicks")));
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName),
            SocialHttp.Get($"{O.GraphVersion}/{credential.ExternalAccountId}?fields=followers_count", credential.AccessToken),
            "Facebook", ct);
        return new AccountMetricSnapshot(InsightsJson.Long(json, "followers_count"), 0, 0);
    }

    internal static long Pick(Dictionary<string, long> values, params string[] names) =>
        names.Select(n => values.GetValueOrDefault(n)).FirstOrDefault(v => v > 0);
}

/// <summary>Instagram：GET /{media-id}/insights（instagram_manage_insights 権限）。</summary>
public sealed class InstagramInsightsReader(IHttpClientFactory http, IOptions<SocialOptions> options) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.Instagram;
    public bool IsSimulation => false;
    private MetaOptions O => options.Value.Meta;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var id in externalPostIds)
        {
            var m = InsightsJson.GraphInsights(await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{O.GraphVersion}/{Uri.EscapeDataString(id)}/insights?{SocialHttp.Query(("metric", O.InstagramMediaMetrics))}",
                credential.AccessToken), "Instagram", ct));
            result.Add(new PostMetricSnapshot(id,
                Impressions: FacebookInsightsReader.Pick(m, "views", "impressions"),
                Reach: m.GetValueOrDefault("reach"),
                Views: m.GetValueOrDefault("views"),
                Likes: (int)m.GetValueOrDefault("likes"),
                Comments: (int)m.GetValueOrDefault("comments"),
                Shares: (int)m.GetValueOrDefault("shares"),
                Saves: (int)m.GetValueOrDefault("saved"),
                LinkClicks: 0,
                ProfileVisits: (int)m.GetValueOrDefault("profile_visits"),
                Follows: (int)m.GetValueOrDefault("follows")));
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName),
            SocialHttp.Get($"{O.GraphVersion}/{credential.ExternalAccountId}?fields=followers_count", credential.AccessToken),
            "Instagram", ct);
        return new AccountMetricSnapshot(InsightsJson.Long(json, "followers_count"), 0, 0);
    }
}

/// <summary>Threads：GET /{media-id}/insights（threads_manage_insights 権限）、フォロワーは /{user-id}/threads_insights。</summary>
public sealed class ThreadsInsightsReader(IHttpClientFactory http, IOptions<SocialOptions> options) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.Threads;
    public bool IsSimulation => false;
    private ThreadsOptions O => options.Value.Threads;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(ThreadsConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var id in externalPostIds)
        {
            var m = InsightsJson.GraphInsights(await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{O.ApiVersion}/{Uri.EscapeDataString(id)}/insights?{SocialHttp.Query(("metric", O.MediaMetrics))}",
                credential.AccessToken), "Threads", ct));
            result.Add(new PostMetricSnapshot(id,
                Impressions: m.GetValueOrDefault("views"),
                Reach: 0,
                Views: m.GetValueOrDefault("views"),
                Likes: (int)m.GetValueOrDefault("likes"),
                Comments: (int)m.GetValueOrDefault("replies"),
                Shares: (int)(m.GetValueOrDefault("reposts") + m.GetValueOrDefault("quotes") + m.GetValueOrDefault("shares")),
                Saves: 0,
                LinkClicks: 0));
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var m = InsightsJson.GraphInsights(await SocialHttp.SendAsync(http.CreateClient(ThreadsConnector.HttpClientName),
            SocialHttp.Get($"{O.ApiVersion}/{credential.ExternalAccountId}/threads_insights?metric=followers_count",
                credential.AccessToken), "Threads", ct));
        return new AccountMetricSnapshot(m.GetValueOrDefault("followers_count"), 0, 0);
    }
}

/// <summary>
/// LINE：一斉配信の投稿単位の指標は配信のリクエスト ID 単位の統計（翌日以降・20人以上）でしか取れないため取得しない。
/// 友だち数は GET /v2/bot/insight/followers?date=yyyyMMdd（前日分まで）。
/// </summary>
public sealed class LineInsightsReader(IHttpClientFactory http) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.Line;
    public bool IsSimulation => false;

    public Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct) => Task.FromResult<IReadOnlyList<PostMetricSnapshot>>([]);

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(LineConnector.HttpClientName),
            SocialHttp.Get($"v2/bot/insight/followers?date={date.AddDays(-1):yyyyMMdd}", credential.AccessToken), "LINE", ct);
        return new AccountMetricSnapshot(InsightsJson.Long(json, "followers"), 0, 0);
    }
}

public sealed class InsightsReaderFactory(IEnumerable<ISocialInsightsReader> readers) : IInsightsReaderFactory
{
    private readonly ISocialInsightsReader[] _all = [.. readers];

    public ISocialInsightsReader? Get(SocialPlatform platform, bool demo) =>
        _all.LastOrDefault(r => r.Platform == platform && r.IsSimulation == demo);
}
