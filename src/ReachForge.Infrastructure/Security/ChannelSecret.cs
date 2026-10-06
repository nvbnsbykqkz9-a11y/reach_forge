using ReachForge.Domain.Common;

namespace ReachForge.Infrastructure.Security;

/// <summary>暗号化した SNS トークン（Key Vault を使わない環境用）。Data Protection で暗号化し、平文は保存しない。</summary>
public sealed class ChannelSecret : Entity
{
    public Guid ChannelId { get; set; }
    public required string Protected { get; set; }
}
