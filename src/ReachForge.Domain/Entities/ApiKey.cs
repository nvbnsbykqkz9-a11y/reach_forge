using System.Security.Cryptography;
using System.Text;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>
/// 外部連携用の API キー（RF-DES-001 13章：スコープ付き）。キー本体は作成時に一度だけ表示し、DB にはハッシュだけを持つ。
/// 権限はロールで表す（オーナー・管理者の権限は付けられない）。
/// </summary>
public sealed class ApiKey : Entity
{
    public const string KeyPrefix = "rfk_";
    public static readonly IReadOnlyList<Role> AllowedRoles = [Role.Editor, Role.Approver, Role.Responder, Role.Viewer];

    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>キーの見分け用（先頭12文字）。画面に表示する。</summary>
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public Role Role { get; set; } = Role.Viewer;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string CreatedBy { get; set; } = "";

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    /// <summary>新しいキー（本体）を作る：rfk_ ＋ 英数字 40文字。</summary>
    public static string NewSecret()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return KeyPrefix + RandomNumberGenerator.GetString(alphabet, 40);
    }

    /// <summary>キー本体のハッシュ（十分な長さの乱数なのでソルトなしの SHA-256 で照合できる）。</summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string PrefixOf(string secret) => secret.Length >= 12 ? secret[..12] : secret;
}

/// <summary>POST の冪等性（Idempotency-Key、24時間保持）。同じキーの再送には最初の応答を返す。</summary>
public sealed class IdempotencyRecord : Entity
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>呼び出し元（利用者 ID または API キー ID）＋キー。</summary>
    public required string Scope { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }

    /// <summary>0 は処理中。</summary>
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public byte[]? Body { get; set; }
    public string? Location { get; set; }
}
