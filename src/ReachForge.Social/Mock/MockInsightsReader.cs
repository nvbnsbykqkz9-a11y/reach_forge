using ReachForge.Application.Social;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Mock;

/// <summary>
/// デモ接続用の擬似指標。投稿 ID から決まる最終値に向けて、公開からの経過時間に応じて増える（単調増加・決定的）。
/// 公開時刻は MockPublisher が採番する ID（UUID v7）に含まれる時刻から求める。
/// </summary>
public sealed class MockInsightsReader(SocialPlatform platform, TimeProvider clock) : ISocialInsightsReader
{
    public SocialPlatform Platform { get; } = platform;
    public bool IsSimulation => true;

    public Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        IReadOnlyList<PostMetricSnapshot> result = externalPostIds.Select(id =>
        {
            var seed = Seed(id);
            var hours = PublishedAt(id) is { } at ? Math.Max(0, (now - at).TotalHours) : 72;
            var growth = 1 - Math.Exp(-hours / 24.0); // 24時間で約63%、72時間で約95%
            var impressions = (long)((800 + seed % 2400) * growth);
            var likes = (int)(impressions * (0.02 + seed % 40 / 1000.0));
            return new PostMetricSnapshot(id, impressions, (long)(impressions * 0.7), Platform is SocialPlatform.TikTok ? impressions : 0,
                likes, likes / 8, likes / 12, likes / 10, (int)(impressions * 0.006), likes / 20, likes / 30);
        }).ToList();
        return Task.FromResult(result);
    }

    public Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct) =>
        Task.FromResult(new AccountMetricSnapshot(1200 + Seed(credential.ExternalAccountId) % 300 + date.DayNumber % 365 * 3, 0, 0));

    /// <summary>"mock-x-{uuid v7}" の先頭48ビット（UNIX ミリ秒）。</summary>
    internal static DateTimeOffset? PublishedAt(string externalId)
    {
        var hex = externalId[(externalId.LastIndexOf('-') + 1)..];
        if (hex.Length != 32 || !Guid.TryParseExact(hex, "N", out var g) || g.Version != 7) return null;
        var ms = Convert.ToInt64(hex[..12], 16);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    private static long Seed(string value)
    {
        unchecked
        {
            long h = 17;
            foreach (var ch in value) h = h * 31 + ch;
            return Math.Abs(h % 100_000);
        }
    }
}
