using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using ReachForge.Application.Social;

namespace ReachForge.Infrastructure.Security;

/// <summary>
/// OAuth の state・PKCE と、アカウント選択待ちの一時保管（10分、F-01-1）。
/// 選択待ちにはトークンが含まれるため暗号化して保存する。複数インスタンスでは Redis を使う。
/// </summary>
public sealed class DistributedOAuthStateStore(IDistributedCache cache, IDataProtectionProvider protection) : IOAuthStateStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("ReachForge.OAuthState.v1");

    public Task SaveAsync(string state, PendingAuthorization pending, CancellationToken ct) =>
        SetAsync($"rf:oauth:state:{state}", pending, ct);

    public async Task<PendingAuthorization?> TakeAsync(string state, CancellationToken ct)
    {
        var key = $"rf:oauth:state:{state}";
        var value = await GetAsync<PendingAuthorization>(key, ct);
        await cache.RemoveAsync(key, ct); // 1回限り
        return value;
    }

    public Task SaveSelectionAsync(string key, PendingSelection selection, CancellationToken ct) =>
        SetAsync($"rf:oauth:sel:{key}", selection, ct);

    public Task<PendingSelection?> GetSelectionAsync(string key, CancellationToken ct) =>
        GetAsync<PendingSelection>($"rf:oauth:sel:{key}", ct);

    public Task RemoveSelectionAsync(string key, CancellationToken ct) => cache.RemoveAsync($"rf:oauth:sel:{key}", ct);

    private Task SetAsync<T>(string key, T value, CancellationToken ct) =>
        cache.SetStringAsync(key, _protector.Protect(JsonSerializer.Serialize(value, s_json)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime }, ct);

    private async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
    {
        var raw = await cache.GetStringAsync(key, ct);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(_protector.Unprotect(raw), s_json);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
