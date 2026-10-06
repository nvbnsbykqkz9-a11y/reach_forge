using System.Security.Cryptography;
using System.Text;

namespace ReachForge.Social.Webhooks;

/// <summary>
/// Webhook の署名検証（RF-DES-001 5.5：HMAC-SHA256）。比較は固定時間で行う。
/// </summary>
public static class WebhookSignature
{
    /// <summary>Meta（Facebook / Instagram / Threads）：X-Hub-Signature-256: sha256=&lt;hex&gt;（App Secret で署名）。</summary>
    public static bool VerifyMeta(ReadOnlySpan<byte> body, string? header, string appSecret)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(header["sha256=".Length..]);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body), expected);
    }

    /// <summary>LINE：X-Line-Signature: base64(HMAC-SHA256(チャネルシークレット, 本文))。</summary>
    public static bool VerifyLine(ReadOnlySpan<byte> body, string? header, string channelSecret)
    {
        if (string.IsNullOrEmpty(header)) return false;
        Span<byte> expected = stackalloc byte[32];
        if (!Convert.TryFromBase64String(header, expected, out var written) || written != 32) return false;
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body), expected);
    }
}
