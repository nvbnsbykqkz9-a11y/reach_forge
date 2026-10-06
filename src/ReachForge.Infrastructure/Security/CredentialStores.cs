using System.Text.Json;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Social;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Security;

internal static class TokenJson
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    public static string Serialize(StoredToken token) => JsonSerializer.Serialize(token, s_json);
    public static StoredToken? Deserialize(string json) => JsonSerializer.Deserialize<StoredToken>(json, s_json);
}

/// <summary>
/// DB 保存の資格情報ストア。トークンは Data Protection（鍵は DB に保存し Web・Worker で共有）で暗号化する。
/// 本番は <see cref="KeyVaultCredentialStore"/> を使う（RF-DES-001 9.1）。
/// </summary>
public sealed class EncryptedDbCredentialStore(ReachForgeDbContext db, IDataProtectionProvider protection) : ICredentialStore
{
    private const string Prefix = "db://";
    private readonly IDataProtector _protector = protection.CreateProtector("ReachForge.ChannelTokens.v1");

    public async Task<string> SaveAsync(Guid tenantId, Guid channelId, StoredToken token, string? existingRef, CancellationToken ct)
    {
        var protectedValue = _protector.Protect(TokenJson.Serialize(token));
        ChannelSecret? secret = null;
        if (existingRef is not null && existingRef.StartsWith(Prefix) && Guid.TryParse(existingRef[Prefix.Length..], out var id))
        {
            secret = await db.Set<ChannelSecret>().FirstOrDefaultAsync(s => s.Id == id, ct);
        }
        if (secret is null)
        {
            secret = new ChannelSecret { TenantId = tenantId, ChannelId = channelId, Protected = protectedValue };
            db.Add(secret);
        }
        secret.Protected = protectedValue;
        await db.SaveChangesAsync(ct);
        return Prefix + secret.Id;
    }

    public async Task<StoredToken?> LoadAsync(string secretRef, CancellationToken ct)
    {
        if (!secretRef.StartsWith(Prefix) || !Guid.TryParse(secretRef[Prefix.Length..], out var id)) return null;
        var secret = await db.Set<ChannelSecret>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (secret is null) return null;
        try
        {
            return TokenJson.Deserialize(_protector.Unprotect(secret.Protected));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // 鍵の喪失など → 要再接続として扱う
        }
    }

    public async Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (!secretRef.StartsWith(Prefix) || !Guid.TryParse(secretRef[Prefix.Length..], out var id)) return;
        var secret = await db.Set<ChannelSecret>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (secret is null) return;
        db.Remove(secret);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Azure Key Vault の資格情報ストア。Managed Identity（DefaultAzureCredential）で取得する。</summary>
public sealed class KeyVaultCredentialStore(SecretClient client) : ICredentialStore
{
    private const string Prefix = "kv://";

    public async Task<string> SaveAsync(Guid tenantId, Guid channelId, StoredToken token, string? existingRef, CancellationToken ct)
    {
        var name = $"rf-channel-{channelId:N}";
        var secret = new KeyVaultSecret(name, TokenJson.Serialize(token));
        secret.Properties.Tags["tenant"] = tenantId.ToString();
        secret.Properties.ContentType = "application/json";
        await client.SetSecretAsync(secret, ct);
        return Prefix + name;
    }

    public async Task<StoredToken?> LoadAsync(string secretRef, CancellationToken ct)
    {
        if (!secretRef.StartsWith(Prefix)) return null;
        try
        {
            var secret = await client.GetSecretAsync(secretRef[Prefix.Length..], cancellationToken: ct);
            return TokenJson.Deserialize(secret.Value.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (!secretRef.StartsWith(Prefix)) return;
        try
        {
            await client.StartDeleteSecretAsync(secretRef[Prefix.Length..], ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
