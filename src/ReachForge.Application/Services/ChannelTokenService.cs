using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>
/// チャネルの資格情報の取得と更新（F-01-6 TokenRefreshJob）。
/// 期限が近いトークンは使用前に更新し、更新できなければチャネルを「要再接続」にして通知する。
/// </summary>
public sealed class ChannelTokenService(
    IAppDbContext db,
    IEnumerable<IChannelConnector> connectors,
    ICredentialStore credentials,
    TimeProvider clock,
    ILogger<ChannelTokenService> log)
{
    /// <summary>使用直前に更新する余裕（X のアクセストークンは約2時間で失効する）。</summary>
    public static readonly TimeSpan UseRefreshWindow = TimeSpan.FromMinutes(5);

    /// <summary>日次ジョブで更新する範囲（期限7日以内）。</summary>
    public static readonly TimeSpan JobRefreshWindow = TimeSpan.FromDays(7);

    public async Task<ChannelCredential> GetCredentialAsync(Channel channel, CancellationToken ct)
    {
        if (channel.IsDemo)
        {
            return new ChannelCredential(channel.Id, channel.Platform, channel.ExternalAccountId, new StoredToken("demo-token"));
        }

        var token = await credentials.LoadAsync(channel.CredentialSecretRef, ct);
        if (token is null)
        {
            await MarkReauthAsync(channel, "token_missing", ct);
            throw Reauth(channel);
        }
        if (token.ExpiresWithin(UseRefreshWindow, clock.GetUtcNow()))
        {
            token = await RefreshAsync(channel, token, ct);
        }
        return new ChannelCredential(channel.Id, channel.Platform, channel.ExternalAccountId, token);
    }

    /// <summary>期限7日以内のトークンを更新する（システムコンテキストで実行）。戻り値は（更新数, 要再接続数）。</summary>
    public async Task<(int Refreshed, int ReauthRequired)> RefreshExpiringAsync(CancellationToken ct)
    {
        var limit = clock.GetUtcNow() + JobRefreshWindow;
        var channels = (await db.Channels
                .Where(c => c.Status == ChannelStatus.Active && !c.IsDemo && c.TokenExpiresAt != null)
                .ToListAsync(ct))
            .Where(c => c.TokenExpiresAt <= limit)
            .ToList();

        int refreshed = 0, reauth = 0;
        foreach (var channel in channels)
        {
            try
            {
                var token = await credentials.LoadAsync(channel.CredentialSecretRef, ct) ?? throw Reauth(channel);
                await RefreshAsync(channel, token, ct);
                refreshed++;
            }
            catch (SocialApiException ex) when (ex.RequiresReauth)
            {
                reauth++;
            }
            catch (SocialApiException ex)
            {
                // 一時的な失敗は次回のジョブで再試行する
                log.LogWarning(ex, "Token refresh failed transiently for channel {ChannelId}", channel.Id);
            }
        }
        return (refreshed, reauth);
    }

    /// <summary>公式 API が認証エラーを返したときに、チャネルを「要再接続」にする。</summary>
    public async Task MarkReauthAsync(Channel channel, string reason, CancellationToken ct)
    {
        if (channel.Status == ChannelStatus.ReauthRequired) return;
        channel.Status = ChannelStatus.ReauthRequired;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = channel.TenantId, Actor = "system", Action = "channel.reauth_required",
            TargetType = nameof(Channel), TargetId = channel.Id, Detail = reason,
        });
        await db.SaveChangesAsync(ct);
        // TODO(12章): 「SNS再接続が必要」通知（アプリ内・プッシュ・メール、オフ不可）
    }

    private async Task<StoredToken> RefreshAsync(Channel channel, StoredToken token, CancellationToken ct)
    {
        var connector = connectors.FirstOrDefault(c => c.Supports(channel.Platform) && c.Mode != ConnectMode.Demo)
                        ?? throw new SocialApiException(ErrorCodes.SnsReauthRequired,
                            $"{PlatformCatalog.Get(channel.Platform).DisplayName}の連携設定がありません。", isTransient: false);
        try
        {
            var refreshed = await connector.RefreshAsync(channel.Platform, token, ct);
            channel.CredentialSecretRef = await credentials.SaveAsync(channel.TenantId, channel.Id, refreshed,
                channel.CredentialSecretRef, ct);
            channel.TokenExpiresAt = refreshed.ExpiresAt;
            channel.LastCheckedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return refreshed;
        }
        catch (SocialApiException ex) when (ex.RequiresReauth)
        {
            await MarkReauthAsync(channel, ex.Message, ct);
            throw;
        }
    }

    private static SocialApiException Reauth(Channel channel) =>
        new(ErrorCodes.SnsReauthRequired,
            $"{PlatformCatalog.Get(channel.Platform).DisplayName}の再認証が必要です。チャネル設定から再接続してください。",
            isTransient: false);
}
