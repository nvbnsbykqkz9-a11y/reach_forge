using System.Globalization;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Inbox;
using ReachForge.Social.Insights;

namespace ReachForge.Social.YouTube;

/// <summary>
/// YouTube：GET /youtube/v3/videos?part=statistics（1回50件まで、1単位）で再生数・高評価・コメント数を取得する。
/// 表示回数・シェア数は Data API にないため 0（表示回数は再生数で代替：MetricsCalculator）。
/// 登録者数は channels?part=statistics（非公開にしている場合は 0）。
/// </summary>
public sealed class YouTubeInsightsReader(IHttpClientFactory http) : ISocialInsightsReader
{
    public SocialPlatform Platform => SocialPlatform.YouTube;
    public bool IsSimulation => false;

    public async Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(YouTubeConnector.HttpClientName);
        var result = new List<PostMetricSnapshot>();
        foreach (var chunk in externalPostIds.Chunk(50))
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"youtube/v3/videos?{SocialHttp.Query(("part", "statistics"), ("id", string.Join(',', chunk)))}", credential.AccessToken),
                "YouTube", ct);
            foreach (var v in InboxJson.Items(json, "items"))
            {
                if (SocialHttp.StrOrNull(v, "id") is not { } id) continue;
                result.Add(new PostMetricSnapshot(id,
                    Impressions: 0,
                    Reach: 0,
                    Views: InsightsJson.Long(v, "statistics.viewCount"),
                    Likes: InsightsJson.Int(v, "statistics.likeCount"),
                    Comments: InsightsJson.Int(v, "statistics.commentCount"),
                    Shares: 0,
                    Saves: InsightsJson.Int(v, "statistics.favoriteCount"),
                    LinkClicks: 0));
            }
        }
        return result;
    }

    public async Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(YouTubeConnector.HttpClientName), SocialHttp.Get(
            $"youtube/v3/channels?part=statistics&id={Uri.EscapeDataString(credential.ExternalAccountId)}", credential.AccessToken),
            "YouTube", ct);
        var channel = InboxJson.Items(json, "items").FirstOrDefault();
        return new AccountMetricSnapshot(InsightsJson.Long(channel, "statistics.subscriberCount"), 0, 0);
    }
}

/// <summary>
/// YouTube のコメント：直近に公開した動画ごとに GET /youtube/v3/commentThreads?videoId=…（1件1単位）。
/// 1日のクォータ（既定 10,000 単位）を使い切らないよう、取得間隔は15分にしている（InboxService.PollInterval）。
/// 返信は comments.insert（parentId はトップレベルのコメント ID、50単位）、非表示は setModerationStatus=heldForReview（確認待ちへ戻す）。
/// </summary>
public sealed class YouTubeInboxReader(IHttpClientFactory http, TimeProvider clock) : ISocialInboxReader
{
    public const int MaxResults = 20;
    public SocialPlatform Platform => SocialPlatform.YouTube;
    public bool IsSimulation => false;

    public async Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(YouTubeConnector.HttpClientName);
        var result = new List<InboxItem>();
        foreach (var videoId in recentPostIds)
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get("youtube/v3/commentThreads?" + SocialHttp.Query(
                ("part", "snippet"), ("videoId", videoId), ("order", "time"), ("textFormat", "plainText"),
                ("maxResults", MaxResults.ToString(CultureInfo.InvariantCulture))), credential.AccessToken), "YouTube", ct);
            foreach (var thread in InboxJson.Items(json, "items"))
            {
                var comment = thread["snippet"]?["topLevelComment"];
                if (comment is null) continue;
                var s = comment["snippet"];
                var author = SocialHttp.StrOrNull(s, "authorChannelId.value") ?? "";
                // 自分のチャンネルのコメント（返信の確認など）は取り込まない
                if (author == credential.ExternalAccountId) continue;
                var item = new InboxItem(SocialHttp.Str(comment, "id"), Platform, InboxKind.Comment, author,
                    SocialHttp.StrOrNull(s, "authorDisplayName") ?? "YouTube",
                    SocialHttp.StrOrNull(s, "textOriginal") ?? SocialHttp.StrOrNull(s, "textDisplay") ?? "",
                    InboxJson.Time(SocialHttp.StrOrNull(s, "publishedAt"), clock.GetUtcNow()), videoId);
                if (item.ReceivedAt >= since) result.Add(item);
            }
        }
        return result;
    }

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(YouTubeConnector.HttpClientName), SocialHttp.Json(HttpMethod.Post,
            "youtube/v3/comments?part=snippet", new { snippet = new { parentId = target.ExternalId, textOriginal = text } },
            credential.AccessToken), "YouTube", ct);
        return SocialHttp.StrOrNull(json, "id");
    }

    public async Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct)
    {
        await SocialHttp.SendAsync(http.CreateClient(YouTubeConnector.HttpClientName), new HttpRequestMessage(HttpMethod.Post,
            "youtube/v3/comments/setModerationStatus?" + SocialHttp.Query(("id", target.ExternalId), ("moderationStatus", "heldForReview")))
        {
            Headers = { Authorization = new("Bearer", credential.AccessToken) },
        }, "YouTube", ct);
        return true;
    }
}
