using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>連携済みの SNS アカウント1件（RF-DES-001 6.2 (1)）。</summary>
public sealed class Channel : Entity
{
    public Guid WorkspaceId { get; set; }
    public SocialPlatform Platform { get; set; }
    public required string ExternalAccountId { get; set; }
    public required string DisplayName { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>Key Vault のシークレット名。トークン本体は DB に保持しない。</summary>
    public required string CredentialSecretRef { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }
    public ChannelStatus Status { get; set; } = ChannelStatus.Active;
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>トークン期限まで7日以内なら「まもなく再接続が必要です」を表示する（RF-UX-001 SCR-03）。</summary>
    public bool IsTokenExpiringSoon(DateTimeOffset now) =>
        TokenExpiresAt is { } exp && exp - now <= TimeSpan.FromDays(7);
}
