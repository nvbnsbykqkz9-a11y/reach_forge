using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social.Mock;

/// <summary>
/// デモ接続（OAuth を行わずにモックのアカウントを連携する）。Local / Dev 環境専用。
/// 本番の OAuth（state・PKCE を Redis に10分保存 → 認可 → コールバックで交換）は SNS 別コネクタで実装する（F-01）。
/// </summary>
public sealed class DemoChannelConnector(TimeProvider clock) : IChannelConnector
{
    public bool Supports(SocialPlatform platform) => true;

    public Task<string?> BeginAsync(SocialPlatform platform, Guid workspaceId, string callbackUrl, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<ConnectedAccount> CompleteAsync(SocialPlatform platform, string? code, string? state, CancellationToken ct)
    {
        var name = PlatformCatalog.Get(platform).DisplayName;
        return Task.FromResult(new ConnectedAccount(
            ExternalAccountId: $"demo-{platform.ToString().ToLowerInvariant()}",
            DisplayName: $"@demo_{platform.ToString().ToLowerInvariant()}（{name}デモ）",
            AvatarUrl: null,
            AccessToken: $"demo-token-{Guid.NewGuid():N}",
            ExpiresAt: clock.GetUtcNow().AddDays(60),
            Scopes: ["publish", "read_insights"]));
    }
}
