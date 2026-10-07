using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social.Mock;

/// <summary>
/// デモ接続（OAuth を行わずにモックのアカウントを連携する）。Social:UseMock が true の環境で、
/// 公式 API の設定がない SNS にだけ使われる（実コネクタが優先）。
/// </summary>
public sealed class DemoChannelConnector(TimeProvider clock, IOptions<SocialOptions> options) : IChannelConnector
{
    public bool Supports(SocialPlatform platform) => options.Value.UseMock;
    public ConnectMode Mode => ConnectMode.Demo;

    public Task<IReadOnlyList<ConnectedAccount>> ConnectAsync(SocialPlatform platform,
        IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        var name = PlatformCatalog.Get(platform).DisplayName;
        IReadOnlyList<ConnectedAccount> accounts =
        [
            new ConnectedAccount(platform,
                ExternalAccountId: $"demo-{platform.ToString().ToLowerInvariant()}",
                DisplayName: $"@demo_{platform.ToString().ToLowerInvariant()}（{name}デモ）",
                AvatarUrl: null,
                Token: new StoredToken("demo-token", null, clock.GetUtcNow().AddDays(60)),
                Scopes: ["publish", "read_insights"],
                IsDemo: true),
        ];
        return Task.FromResult(accounts);
    }
}
