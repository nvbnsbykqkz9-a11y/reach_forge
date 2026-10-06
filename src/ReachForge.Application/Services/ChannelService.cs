using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>連携開始の結果。OAuth なら認可 URL、資格情報入力型なら入力項目、デモなら登録済みチャネルを返す。</summary>
public sealed record ConnectStart(ConnectMode Mode, string? AuthorizationUrl, IReadOnlyList<CredentialField> Fields,
    Channel? Connected);

/// <summary>連携完了の結果。複数アカウントが返った場合（Meta のページ一覧など）は選択キーを返す。</summary>
public sealed record ConnectOutcome(Channel? Channel, string? SelectionKey);

/// <summary>SNS アカウント連携（F-01 / SCR-03）。</summary>
public sealed class ChannelService(
    IAppDbContext db,
    ITenantContext tenant,
    IEnumerable<IChannelConnector> connectors,
    ICredentialStore credentials,
    IOAuthStateStore oauthState,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<Channel>> ListAsync(CancellationToken ct) =>
        await db.Channels
            .Where(c => c.WorkspaceId == tenant.WorkspaceId && c.Status != ChannelStatus.Revoked)
            .OrderBy(c => c.Platform)
            .ToListAsync(ct);

    public ConnectMode ModeFor(SocialPlatform platform) => ConnectorFor(platform).Mode;

    /// <summary>
    /// 連携を開始する（F-01-1）。state（CSRF 対策）と PKCE の code_verifier を生成して10分保存し、認可 URL を返す。
    /// </summary>
    public async Task<ConnectStart> BeginConnectAsync(SocialPlatform platform, string redirectUri, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var connector = ConnectorFor(platform);
        switch (connector.Mode)
        {
            case ConnectMode.Demo:
                var accounts = await connector.ConnectAsync(platform, new Dictionary<string, string>(), ct);
                return new ConnectStart(ConnectMode.Demo, null, [], await RegisterAsync(accounts[0], ct));
            case ConnectMode.Credentials:
                return new ConnectStart(ConnectMode.Credentials, null, connector.CredentialFields, null);
        }

        var state = RandomToken(32);
        var verifier = RandomToken(64);
        await oauthState.SaveAsync(state,
            new PendingAuthorization(tenant.TenantId, tenant.WorkspaceId, tenant.UserName, platform, verifier, redirectUri), ct);
        var url = connector.BuildAuthorizationUrl(platform, state, CodeChallenge(verifier), redirectUri);
        return new ConnectStart(ConnectMode.OAuth, url, [], null);
    }

    /// <summary>OAuth コールバック（F-01-2〜4）。state を検証し、認可コードをトークンに交換して登録する。</summary>
    public async Task<ConnectOutcome> CompleteOAuthAsync(SocialPlatform platform, string? code, string? state, string? error,
        CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var pending = string.IsNullOrEmpty(state) ? null : await oauthState.TakeAsync(state, ct);
        if (pending is null || pending.Platform != platform || pending.TenantId != tenant.TenantId ||
            pending.WorkspaceId != tenant.WorkspaceId || pending.UserName != tenant.UserName)
        {
            throw new DomainException(ErrorCodes.SnsAuthCanceled,
                "連携の有効期限が切れたか、別の画面から開始された連携です。もう一度「連携する」からお試しください。");
        }
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            throw new DomainException(ErrorCodes.SnsAuthCanceled, "SNSとの連携がキャンセルされました。もう一度お試しください。");
        }

        var accounts = await ConnectorFor(platform).ExchangeAsync(platform, code, pending.CodeVerifier, pending.RedirectUri, ct);
        return await ResolveAsync(platform, accounts, ct);
    }

    /// <summary>資格情報入力型の連携（LINE 公式アカウント）。</summary>
    public async Task<ConnectOutcome> ConnectWithCredentialsAsync(SocialPlatform platform,
        IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var connector = ConnectorFor(platform);
        foreach (var f in connector.CredentialFields.Where(f => f.Required))
        {
            if (!fields.TryGetValue(f.Key, out var v) || string.IsNullOrWhiteSpace(v))
            {
                throw new DomainException(ErrorCodes.Validation, $"「{f.Label}」を入力してください。");
            }
        }
        var accounts = await connector.ConnectAsync(platform, fields, ct);
        return await ResolveAsync(platform, accounts, ct);
    }

    public async Task<PendingSelection?> GetSelectionAsync(string key, CancellationToken ct)
    {
        var selection = await oauthState.GetSelectionAsync(key, ct);
        return selection is not null && selection.TenantId == tenant.TenantId && selection.UserName == tenant.UserName
            ? selection
            : null;
    }

    /// <summary>複数アカウントから1つを選んで登録する（Meta：ページ／IG ビジネスアカウントの選択）。</summary>
    public async Task<Channel> SelectAccountAsync(string key, string externalAccountId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var selection = await GetSelectionAsync(key, ct)
                        ?? throw new DomainException(ErrorCodes.SnsAuthCanceled, "選択の有効期限が切れました。もう一度連携してください。");
        var account = selection.Accounts.FirstOrDefault(a => a.ExternalAccountId == externalAccountId)
                      ?? throw new NotFoundException("アカウント");
        var channel = await RegisterAsync(account, ct);
        await oauthState.RemoveSelectionAsync(key, ct);
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

        if (!channel.IsDemo) await credentials.DeleteAsync(channel.CredentialSecretRef, ct);
        channel.Status = ChannelStatus.Revoked;
        db.Record(tenant, "channel.disconnected", nameof(Channel), channel.Id, $"held={pending.Count}");
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    private async Task<ConnectOutcome> ResolveAsync(SocialPlatform platform, IReadOnlyList<ConnectedAccount> accounts,
        CancellationToken ct)
    {
        var candidates = accounts.Where(a => a.Platform == platform).ToList();
        if (candidates.Count == 0)
        {
            throw new DomainException(platform == SocialPlatform.Instagram ? ErrorCodes.InstagramBusinessRequired : ErrorCodes.SnsAuthCanceled,
                platform == SocialPlatform.Instagram
                    ? "Instagramのビジネスまたはクリエイターアカウントが必要です。Facebookページとつながったプロアカウントに切り替えてから、もう一度連携してください。"
                    : platform == SocialPlatform.Facebook
                        ? "管理しているFacebookページが見つかりませんでした。個人プロフィールには投稿できないため、Facebookページを作成してから連携してください。"
                        : "連携できるアカウントが見つかりませんでした。");
        }
        if (candidates.Count == 1)
        {
            return new ConnectOutcome(await RegisterAsync(candidates[0], ct), null);
        }

        var key = RandomToken(16);
        await oauthState.SaveSelectionAsync(key,
            new PendingSelection(tenant.TenantId, tenant.WorkspaceId, tenant.UserName, platform, candidates), ct);
        return new ConnectOutcome(null, key);
    }

    /// <summary>トークンを資格情報ストアに保存し、チャネルを登録（または再接続）する。</summary>
    private async Task<Channel> RegisterAsync(ConnectedAccount account, CancellationToken ct)
    {
        // 同一 SNS アカウントは同一テナント内で1ワークスペースにのみ接続可（重複投稿防止）
        var existing = await db.Channels.FirstOrDefaultAsync(c =>
            c.Platform == account.Platform && c.ExternalAccountId == account.ExternalAccountId && c.Status != ChannelStatus.Revoked, ct);
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
            Platform = account.Platform,
            ExternalAccountId = account.ExternalAccountId,
            DisplayName = account.DisplayName,
            CredentialSecretRef = "",
        };
        if (existing is null) db.Channels.Add(channel);

        channel.DisplayName = account.DisplayName;
        channel.AvatarUrl = account.AvatarUrl;
        channel.IsDemo = account.IsDemo;
        channel.CredentialSecretRef = account.IsDemo
            ? "demo://"
            : await credentials.SaveAsync(tenant.TenantId, channel.Id, account.Token,
                string.IsNullOrEmpty(channel.CredentialSecretRef) ? null : channel.CredentialSecretRef, ct);
        channel.TokenExpiresAt = account.Token.ExpiresAt;
        channel.Scopes = [.. account.Scopes];
        channel.Status = ChannelStatus.Active;
        channel.LastCheckedAt = clock.GetUtcNow();

        db.Record(tenant, existing is null ? "channel.connected" : "channel.reconnected", nameof(Channel), channel.Id,
            account.Platform.ToString());
        await db.SaveChangesAsync(ct);
        return channel;
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
        ?? throw new DomainException(ErrorCodes.Validation,
            $"{PlatformCatalog.Get(platform).DisplayName}の連携は準備中です（アプリの設定が必要です）。");

    internal static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>PKCE（RFC 7636）の S256 code_challenge。</summary>
    internal static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
