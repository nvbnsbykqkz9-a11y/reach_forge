using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record CreatedApiKey(ApiKey Key, string Secret);

/// <summary>API キーの発行・一覧・失効（オーナー・管理者のみ）。</summary>
public sealed class ApiKeyService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public const int MaxKeysPerWorkspace = 20;

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        return (await db.ApiKeys.AsNoTracking().Where(k => k.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
            .OrderByDescending(k => k.CreatedAt).ToList();
    }

    public async Task<CreatedApiKey> CreateAsync(string name, Role role, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        name = name.Trim();
        if (name.Length is 0 or > 60) throw new DomainException(ErrorCodes.Validation, "キーの名前を60字以内で入力してください（例：基幹システム連携）。");
        if (!ApiKey.AllowedRoles.Contains(role)) throw new DomainException(ErrorCodes.Validation, "API キーにはオーナー・管理者の権限を付けられません。");
        if (expiresAt is { } e && e <= clock.GetUtcNow()) throw new DomainException(ErrorCodes.Validation, "有効期限は未来の日付にしてください。");
        if (await db.ApiKeys.CountAsync(k => k.WorkspaceId == tenant.WorkspaceId && k.RevokedAt == null, ct) >= MaxKeysPerWorkspace)
        {
            throw new DomainException(ErrorCodes.Validation, $"API キーは{MaxKeysPerWorkspace}個までです。使っていないキーを失効させてください。");
        }
        var secret = ApiKey.NewSecret();
        var key = new ApiKey
        {
            TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Name = name, Prefix = ApiKey.PrefixOf(secret),
            SecretHash = ApiKey.Hash(secret), Role = role, ExpiresAt = expiresAt, CreatedBy = tenant.UserName,
        };
        db.ApiKeys.Add(key);
        db.Record(tenant, "apikey.created", nameof(ApiKey), key.Id, $"{name} ({role})");
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key, secret);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageMembers);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.WorkspaceId == tenant.WorkspaceId, ct)
                  ?? throw new NotFoundException("API キー");
        key.RevokedAt ??= clock.GetUtcNow();
        db.Record(tenant, "apikey.revoked", nameof(ApiKey), key.Id, key.Name);
        await db.SaveChangesAsync(ct);
    }
}
