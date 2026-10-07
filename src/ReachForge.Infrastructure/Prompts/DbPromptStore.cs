using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Prompts;

/// <summary>
/// DB のプロンプト（RF-DES-001 4.5）。キーごとの公開中（Active）と候補（Candidate）の版を1分間キャッシュする
/// （ほかのインスタンスでの変更も1分以内に反映される）。並列の生成から呼ばれるため、要求の DbContext は使わない。
/// 候補版は、テナントとキーから決まる 0〜99 の番号が割合未満のテナントだけが使う（RF-DES-001 10.3 段階適用）。
/// </summary>
public sealed class DbPromptStore(DbContextOptions<ReachForgeDbContext> dbOptions, IMemoryCache cache, TimeProvider clock) : IPromptStore
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private sealed record Published(StoredPrompt? Active, StoredPrompt? Candidate, int RolloutPercent);

    public async Task<StoredPrompt?> ResolveAsync(string key, Guid tenantId, CancellationToken ct)
    {
        var published = await cache.GetOrCreateAsync(CacheKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            await using var db = new ReachForgeDbContext(dbOptions, new MutableTenantContext { IsSystem = true, UserName = "prompts" }, null, clock);
            var rows = await db.PromptTemplates.AsNoTracking()
                .Where(p => p.Key == key && (p.Status == PromptStatus.Active || p.Status == PromptStatus.Candidate))
                .ToListAsync(ct);
            var active = rows.Where(p => p.Status == PromptStatus.Active).MaxBy(p => p.Version);
            var candidate = rows.Where(p => p.Status == PromptStatus.Candidate).MaxBy(p => p.Version);
            return new Published(
                active is null ? null : new StoredPrompt(key, active.Version, active.Body),
                candidate is null ? null : new StoredPrompt(key, candidate.Version, candidate.Body),
                candidate?.RolloutPercent ?? 0);
        });
        if (published!.Candidate is { } c && Bucket(tenantId, key) < published.RolloutPercent) return c;
        return published.Active;
    }

    public void Invalidate(string key) => cache.Remove(CacheKey(key));

    /// <summary>テナント×キーで決まる 0〜99 の番号（同じテナントは常に同じ版を使う）。</summary>
    public static int Bucket(Guid tenantId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}:{key}"));
        return (int)(BitConverter.ToUInt32(hash, 0) % 100);
    }

    private static string CacheKey(string key) => $"prompt:{key}";
}
