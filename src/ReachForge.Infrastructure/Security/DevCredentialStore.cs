using ReachForge.Application.Social;
using ReachForge.Domain.Entities;

namespace ReachForge.Infrastructure.Security;

/// <summary>
/// 開発用の資格情報ストア。トークン本体は保存せず、参照キーのみを返す（モック SNS はトークンを使わない）。
/// 本番は Azure Key Vault（Managed Identity で取得、アプリ内は最大5分の暗号化キャッシュ）に差し替える（RF-DES-001 9.1）。
/// </summary>
public sealed class DevCredentialStore : ICredentialStore
{
    public Task<string> SaveAsync(Guid channelId, string accessToken, CancellationToken ct) =>
        Task.FromResult($"dev://channels/{channelId:N}");

    public Task<ChannelCredential> GetAsync(Channel channel, CancellationToken ct) =>
        Task.FromResult(new ChannelCredential(channel.Id, channel.Platform, channel.ExternalAccountId, "dev-token"));

    public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
}
