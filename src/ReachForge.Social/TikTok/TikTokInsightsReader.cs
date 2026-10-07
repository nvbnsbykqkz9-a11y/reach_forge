using System.Text.Json.Nodes;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Social.Insights;

namespace ReachForge.Social.TikTok;

/// <summary>
/// TikTok：POST /v2/video/query/（video.list スコープ、1回20件まで）で再生数・いいね・コメント・シェア数を取得する。
/// 表示回数は提供されないため再生数で代替する（MetricsCalculator）。フォロワー数は /v2/user/info/ の follower_count（user.info.stats）。
/// コメントの取得 API は一般のアプリに提供されていないため、受信箱には対応しない。
/// </summary>
public sealed class TikTokInsightsReader(IHttpClientFactory http) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.TikTok;
    public bool IsSimulation => false;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(TikTokConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var chunk in externalPostIds.Chunk(20))
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post,
                "v2/video/query/?fields=id,view_count,like_count,comment_count,share_count",
                new { filters = new { video_ids = chunk } }, credential.AccessToken), "TikTok", ct);
            if (json["data"]?["videos"] is not JsonArray videos) continue;
            foreach (var v in videos)
            {
                if (SocialHttp.StrOrNull(v, "id") is not { } id) continue;
                result.Add(new PostMetricSnapshot(id,
                    Impressions: 0,
                    Reach: 0,
                    Views: InsightsJson.Long(v, "view_count"),
                    Likes: InsightsJson.Int(v, "like_count"),
                    Comments: InsightsJson.Int(v, "comment_count"),
                    Shares: InsightsJson.Int(v, "share_count"),
                    Saves: 0,
                    LinkClicks: 0));
            }
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(TikTokConnector.HttpClientName),
            SocialHttp.Get("v2/user/info/?fields=follower_count", credential.AccessToken), "TikTok", ct);
        return new AccountMetricSnapshot(InsightsJson.Long(json, "data.user.follower_count"), 0, 0);
    }
}
