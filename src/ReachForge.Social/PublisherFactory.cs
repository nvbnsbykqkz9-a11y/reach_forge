using ReachForge.Application.Social;
using ReachForge.Domain.Enums;

namespace ReachForge.Social;

public sealed class PublisherFactory(IEnumerable<ISocialPublisher> publishers) : IPublisherFactory
{
    private readonly Dictionary<SocialPlatform, ISocialPublisher> _byPlatform =
        publishers.GroupBy(p => p.Platform).ToDictionary(g => g.Key, g => g.Last());

    public ISocialPublisher Get(SocialPlatform platform) =>
        _byPlatform.TryGetValue(platform, out var p)
            ? p
            : throw new SocialApiException("E-PUB-010", $"{platform} への投稿機能は準備中です。", isTransient: false);
}
