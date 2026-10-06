using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>SNS アカウント連携（F-01 / SCR-03）。</summary>
public sealed class ChannelService(
    IAppDbContext db,
    ITenantContext tenant,
    IEnumerable<IChannelConnector> connectors,
    ICredentialStore credentials,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<Channel>> ListAsync(CancellationToken ct) =>
        await db.Channels
            .Where(c => c.WorkspaceId == tenant.WorkspaceId && c.Status != ChannelStatus.Revoked)
            .OrderBy(c => c.Platform)
            .ToListAsync(ct);

    /// <summary>連携を開始する。認可 URL を返す（デモ接続の場合は null を返し、即時に連携を完了する）。</summary>
    public async Task<string?> BeginConnectAsync(SocialPlatform platform, string callbackUrl, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        await EnsurePlanLimitAsync(ct);
        return await ConnectorFor(platform).BeginAsync(platform, tenant.WorkspaceId, callbackUrl, ct);
    }

    /// <summary>OAuth コールバック（またはデモ接続）で連携を完了し、チャネルを登録する。</summary>
    public async Task<Channel> CompleteConnectAsync(SocialPlatform platform, string? code, string? state, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var account = await ConnectorFor(platform).CompleteAsync(platform, code, state, ct);

        // 同一 SNS アカウントは同一テナント内で1ワークスペースにのみ接続可（重複投稿防止）
        var existing = await db.Channels.FirstOrDefaultAsync(c =>
            c.Platform == platform && c.ExternalAccountId == account.ExternalAccountId && c.Status != ChannelStatus.Revoked, ct);
        if (existing is not null && existing.WorkspaceId != tenant.WorkspaceId)
        {
            throw new DomainException(ErrorCodes.SnsDuplicateAccount,
                "このアカウントは別のワークスペースで連携済みです。重複投稿を防ぐため、1つのワークスペースでのみ連携できます。");
        }

        if (existing is null) await EnsurePlanLimitAsync(ct);

        var channel = existing ?? new Channel
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Platform = platform,
            ExternalAccountId = account.ExternalAccountId,
            DisplayName = account.DisplayName,
            CredentialSecretRef = "",
        };
        if (existing is null) db.Channels.Add(channel);

        channel.DisplayName = account.DisplayName;
        channel.AvatarUrl = account.AvatarUrl;
        channel.CredentialSecretRef = await credentials.SaveAsync(channel.Id, account.AccessToken, ct);
        channel.TokenExpiresAt = account.ExpiresAt;
        channel.Scopes = [.. account.Scopes];
        channel.Status = ChannelStatus.Active;
        channel.LastCheckedAt = clock.GetUtcNow();

        db.Record(tenant, "channel.connected", nameof(Channel), channel.Id, platform.ToString());
        await db.SaveChangesAsync(ct);
        return channel;
    }

    /// <summary>連携を解除する。予約済みの投稿は「保留」に変更する（F-01 業務ルール）。戻り値は保留にした件数。</summary>
    public async Task<int> DisconnectAsync(Guid channelId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct) ?? throw new NotFoundException("チャネル");
        var pending = await db.PostVariants
            .Where(v => v.ChannelId == channelId &&
                        (v.Status == VariantStatus.Scheduled || v.Status == VariantStatus.Approved || v.Status == VariantStatus.InReview))
            .ToListAsync(ct);
        foreach (var v in pending)
        {
            v.Hold($"{PlatformCatalog.Get(channel.Platform).DisplayName}の連携が解除されたため保留にしました。");
        }

        await credentials.DeleteAsync(channel.CredentialSecretRef, ct);
        channel.Status = ChannelStatus.Revoked;
        db.Record(tenant, "channel.disconnected", nameof(Channel), channel.Id, $"held={pending.Count}");
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    private async Task EnsurePlanLimitAsync(CancellationToken ct)
    {
        var max = await db.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.MaxChannels).FirstAsync(ct);
        var count = await db.Channels.CountAsync(c => c.Status != ChannelStatus.Revoked, ct);
        if (count >= max)
        {
            throw new DomainException(ErrorCodes.BilPlanChannelLimit,
                $"ご契約のプランで連携できるSNSアカウントは{max}件までです。プランを変更するか、使っていない連携を解除してください。");
        }
    }

    private IChannelConnector ConnectorFor(SocialPlatform platform) =>
        connectors.FirstOrDefault(c => c.Supports(platform))
        ?? throw new DomainException(ErrorCodes.Validation, $"{PlatformCatalog.Get(platform).DisplayName}の連携は準備中です。");
}
