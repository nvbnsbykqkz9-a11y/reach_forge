using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social;

public sealed class PublisherFactory(IEnumerable<ISocialPublisher> publishers) : IPublisherFactory
{
    private readonly ISocialPublisher[] _all = [.. publishers];

    public ISocialPublisher Get(SocialPlatform platform, bool demo) =>
        _all.LastOrDefault(p => p.Platform == platform && p.IsSimulation == demo)
        ?? throw new SocialApiException(ErrorCodes.PubFailed,
            demo
                ? "デモ接続は無効です。チャネル設定から公式アカウントを連携してください。"
                : $"{PlatformCatalog.Get(platform).DisplayName}への投稿機能は準備中です。",
            isTransient: false);
}
