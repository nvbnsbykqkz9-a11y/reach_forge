using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// アプリ経由の配信 URL（/media/{token}）。トークンは期限付きの Data Protection で暗号化した資産 ID で、改ざん・期限切れは拒否する。
/// </summary>
public sealed class AppMediaUrlSigner(IDataProtectionProvider protection, IOptions<MediaOptions> options, TimeProvider clock)
    : IMediaUrlSigner
{
    public const string Purpose = "ReachForge.MediaUrl.v1";

    public Task<string> CreateReadUrlAsync(Guid mediaAssetId, TimeSpan lifetime, CancellationToken ct)
    {
        var expires = (clock.GetUtcNow() + lifetime).ToUnixTimeSeconds();
        var token = protection.CreateProtector(Purpose).Protect($"{mediaAssetId:N}|{expires}");
        var baseUrl = options.Value.PublicBaseUrl?.TrimEnd('/') ?? "";
        return Task.FromResult($"{baseUrl}/media/{token}");
    }

    /// <summary>トークンを検証して資産 ID を返す。改ざん・期限切れは null。</summary>
    public static Guid? Validate(IDataProtectionProvider protection, string token, TimeProvider clock)
    {
        try
        {
            var parts = protection.CreateProtector(Purpose).Unprotect(token).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[1], out var expires)) return null;
            if (clock.GetUtcNow().ToUnixTimeSeconds() > expires) return null;
            return Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Blob のユーザー委任 SAS（読取専用・短時間）。SNS は Blob から直接画像を取得する。</summary>
public sealed class BlobSasUrlSigner(BlobServiceClient service, IOptions<MediaOptions> options, ReachForgeDbContext db, TimeProvider clock)
    : IMediaUrlSigner
{
    private static UserDelegationKey? s_key;
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    public async Task<string> CreateReadUrlAsync(Guid mediaAssetId, TimeSpan lifetime, CancellationToken ct)
    {
        var path = await db.MediaAssets.Where(m => m.Id == mediaAssetId).Select(m => m.BlobPath).FirstOrDefaultAsync(ct)
                   ?? throw new InvalidOperationException("Media not found.");
        var now = clock.GetUtcNow();
        var key = await KeyAsync(now, ct);
        var builder = new BlobSasBuilder
        {
            BlobContainerName = options.Value.BlobContainer,
            BlobName = path,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = now + lifetime,
            Protocol = SasProtocol.Https,
        };
        builder.SetPermissions(BlobSasPermissions.Read);
        var blob = service.GetBlobContainerClient(options.Value.BlobContainer).GetBlobClient(path);
        return new BlobUriBuilder(blob.Uri) { Sas = builder.ToSasQueryParameters(key, service.AccountName) }.ToUri().ToString();
    }

    private async Task<UserDelegationKey> KeyAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (s_key is { } k && k.SignedExpiresOn > now.AddHours(1)) return k;
        await s_lock.WaitAsync(ct);
        try
        {
            if (s_key is { } k2 && k2.SignedExpiresOn > now.AddHours(1)) return k2;
            s_key = (await service.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.AddHours(6), ct)).Value;
            return s_key;
        }
        finally
        {
            s_lock.Release();
        }
    }
}
