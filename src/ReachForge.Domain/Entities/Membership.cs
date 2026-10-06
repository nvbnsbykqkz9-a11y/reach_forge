using System.Security.Cryptography;
using System.Text;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>利用者のワークスペース所属とロール（RF-DES-001 9.1：ロール＋ワークスペース単位の権限）。</summary>
public sealed class WorkspaceMember : Entity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>認証基盤（ASP.NET Core Identity）の利用者 ID。</summary>
    public Guid UserId { get; set; }
    public required string Email { get; set; }
    public string DisplayName { get; set; } = "";
    public Role Role { get; set; } = Role.Editor;
}

/// <summary>メンバー招待（SCR-15）。招待リンクのトークンはハッシュのみ保存する。</summary>
public sealed class Invitation : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid WorkspaceId { get; set; }
    public required string Email { get; set; }
    public Role Role { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string InvitedBy { get; set; } = "";

    public bool IsUsable(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public void Accept(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new DomainException("E-AUTH-410", "この招待リンクは使えません（期限切れ・使用済み・取り消し済み）。招待した人にもう一度依頼してください。");
        }
        AcceptedAt = now;
    }
}
