using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ads;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>広告をつくる画面の入力（1つの SNS・1つの広告）。</summary>
public sealed record AdDraft
{
    public required SocialPlatform Platform { get; init; }
    public required Guid AdAccountId { get; init; }
    public string? Name { get; init; }
    public AdObjective Objective { get; init; } = AdObjective.Traffic;
    public required decimal DailyBudget { get; init; }
    public required DateTimeOffset StartAt { get; init; }
    public required DateTimeOffset EndAt { get; init; }
    public AdTargeting Targeting { get; init; } = new();
    public required AdCreative Creative { get; init; }
}

public sealed record AdRunResult(int Synced, int Failed);

/// <summary>広告にできる YouTube の動画（ReachForge から投稿したもの）。</summary>
public sealed record YouTubeVideoChoice(string VideoId, string Title, DateTimeOffset? PublishedAt, string? Url);

/// <summary>
/// 有料広告（各社の広告マネージャー）。広告アカウントの連携・AI の広告文・出稿・一時停止・状態と成果の取得。
/// 広告費は各社の広告アカウントに登録された支払い方法へ直接請求される（ReachForge は請求しない）。
/// </summary>
public sealed class AdService(
    IAppDbContext db,
    ITenantContext tenant,
    IAdNetworkFactory networks,
    ICredentialStore credentials,
    IOAuthStateStore oauthState,
    ChannelTokenService channelTokens,
    MediaService media,
    IAdCopyWriter copyWriter,
    IBrandContextProvider brand,
    ICreditService credits,
    TimeProvider clock,
    ILogger<AdService> log)
{
    /// <summary>広告の状態・成果を読み直す間隔（定期処理）。</summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);

    /// <summary>この SNS の広告に使う仕組み（設定がなく、お試しも無効なら null）。</summary>
    public IAdNetworkAdapter? AdapterFor(SocialPlatform platform)
    {
        if (AdNetworks.For(platform) is not { } network) return null;
        return networks.Get(network, demo: false) ?? networks.Get(network, demo: true);
    }

    public async Task<IReadOnlyList<AdAccount>> AccountsAsync(AdNetwork network, CancellationToken ct) =>
        await db.AdAccounts.Where(a => a.Network == network && a.Status != AdAccountStatus.Disconnected).OrderBy(a => a.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<AdCampaign>> ListAsync(SocialPlatform platform, CancellationToken ct) =>
        (await db.AdCampaigns.Where(c => c.Platform == platform).ToListAsync(ct)).OrderByDescending(c => c.CreatedAt).ToList();

    public async Task<IReadOnlyList<AdCampaign>> RecentAsync(int count, CancellationToken ct) =>
        (await db.AdCampaigns.Where(c => c.Status != AdStatus.Draft).ToListAsync(ct)).OrderByDescending(c => c.CreatedAt).Take(count).ToList();

    /// <summary>ReachForge から投稿した YouTube の動画（新しい順）。YouTube の広告は投稿済みの動画で出す。</summary>
    public async Task<IReadOnlyList<YouTubeVideoChoice>> YouTubeVideosAsync(CancellationToken ct) =>
        (await db.PostVariants.Where(v => v.Platform == SocialPlatform.YouTube && v.Status == VariantStatus.Published && v.ExternalPostId != null)
            .ToListAsync(ct))
        .OrderByDescending(v => v.PublishedAt)
        .Take(30)
        .Select(v => new YouTubeVideoChoice(v.ExternalPostId!, v.Title is { Length: > 0 } t ? t : PostText.Truncate(v.Body, 40), v.PublishedAt, v.Url))
        .ToList();

    /// <summary>
    /// 広告アカウントとの連携を始める（各社の認可画面の URL を返す）。お試しの場合はその場でお試しのアカウントをつくり、null を返す。
    /// </summary>
    public async Task<string?> BeginConnectAsync(SocialPlatform platform, string redirectUri, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var adapter = AdapterFor(platform) ?? throw NotAvailable(platform);
        if (adapter.IsSimulation)
        {
            var result = await adapter.ExchangeAsync("demo", "demo", redirectUri, ct);
            await RegisterAsync(adapter, result, ct);
            return null;
        }
        var state = "ad_" + ChannelService.RandomToken(24);
        var verifier = ChannelService.RandomToken(64);
        var start = await adapter.BeginAuthorizationAsync(state, ChannelService.CodeChallenge(verifier), redirectUri, ct);
        await oauthState.SaveAsync(start.StateKey ?? state, new PendingAuthorization(tenant.TenantId, tenant.WorkspaceId, tenant.UserName, platform,
            start.StateSecret ?? verifier, redirectUri), ct);
        return start.Url;
    }

    /// <summary>連携の戻り先：認可コードをトークンに交換し、使える広告アカウントを登録する。</summary>
    /// <returns>連携を始めた SNS（戻る画面を決める）。</returns>
    public async Task<SocialPlatform> CompleteConnectAsync(AdNetwork network, string? code, string? state, string? error, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var pending = string.IsNullOrEmpty(state) ? null : await oauthState.TakeAsync(state, ct);
        if (pending is null || AdNetworks.For(pending.Platform) != network || pending.TenantId != tenant.TenantId
            || pending.WorkspaceId != tenant.WorkspaceId || pending.UserName != tenant.UserName)
        {
            throw new DomainException(ErrorCodes.SnsAuthCanceled, "連携の有効期限が切れたか、別の画面から開始された連携です。もう一度お試しください。");
        }
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            throw new DomainException(ErrorCodes.SnsAuthCanceled, "広告アカウントとの連携がキャンセルされました。もう一度お試しください。");
        }
        var adapter = networks.Get(network, demo: false) ?? throw NotAvailable(pending.Platform);
        var result = await adapter.ExchangeAsync(code, pending.CodeVerifier, pending.RedirectUri, ct);
        await RegisterAsync(adapter, result, ct);
        return pending.Platform;
    }

    private async Task<int> RegisterAsync(IAdNetworkAdapter adapter, AdConnectResult result, CancellationToken ct)
    {
        if (result.Accounts.Count == 0)
        {
            throw new DomainException(ErrorCodes.Validation,
                $"使える広告アカウントがありません。{AdNetworks.DisplayName(adapter.Network)}で広告アカウントと支払い方法を用意してから、もう一度連携してください。");
        }
        var existing = await db.AdAccounts.Where(a => a.Network == adapter.Network).ToListAsync(ct);
        foreach (var info in result.Accounts)
        {
            var account = existing.FirstOrDefault(a => a.ExternalAccountId == info.ExternalAccountId);
            if (account is null)
            {
                account = new AdAccount
                {
                    TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Network = adapter.Network,
                    ExternalAccountId = info.ExternalAccountId, Name = info.Name, CredentialSecretRef = "", IsDemo = adapter.IsSimulation,
                };
                db.AdAccounts.Add(account);
            }
            account.Name = info.Name;
            account.Currency = info.Currency;
            account.Status = AdAccountStatus.Active;
            account.TokenExpiresAt = result.Token.ExpiresAt;
            foreach (var (k, v) in info.Extra ?? new Dictionary<string, string>()) account.Extra[k] = v;
            account.Extra = new Dictionary<string, string>(account.Extra);
            account.CredentialSecretRef = await credentials.SaveAsync(tenant.TenantId, account.Id, result.Token,
                string.IsNullOrEmpty(account.CredentialSecretRef) ? null : account.CredentialSecretRef, ct);
            db.Record(tenant, "ads.account_connected", nameof(AdAccount), account.Id, adapter.Network.ToString());
        }
        await db.SaveChangesAsync(ct);
        return result.Accounts.Count;
    }

    public async Task DisconnectAsync(Guid accountId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageChannels);
        var account = await db.AdAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct) ?? throw new NotFoundException("広告アカウント");
        account.Status = AdAccountStatus.Disconnected;
        if (!string.IsNullOrEmpty(account.CredentialSecretRef)) await credentials.DeleteAsync(account.CredentialSecretRef, ct);
        db.Record(tenant, "ads.account_disconnected", nameof(AdAccount), account.Id, account.Network.ToString());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>AI が広告文の案を3つつくる（投稿文の作成と同じクレジット）。</summary>
    public async Task<IReadOnlyList<AdCopyCandidate>> SuggestCopyAsync(AdCopyRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (string.IsNullOrWhiteSpace(request.Theme)) throw new DomainException(ErrorCodes.Validation, "広告で伝えたいことを入力してください。");
        if (PostText.Length(request.Theme) > 500) throw new DomainException(ErrorCodes.Validation, "伝えたいことは500字以内で入力してください。");
        var ctx = await brand.BuildAsync(tenant.WorkspaceId, [], null, ct);
        await using var hold = await credits.HoldAsync(CreditTable.Cost(CreditOperation.CopyGeneration), ct);
        var result = await copyWriter.WriteAsync(ctx, request, ct);
        await hold.CommitAsync(CreditTable.Cost(CreditOperation.CopyGeneration), ct);
        return result;
    }

    /// <summary>1日の予算の下限（広告アカウントの通貨）。</summary>
    public decimal MinDailyBudget(SocialPlatform platform, string currency) => AdapterFor(platform)?.MinDailyBudget(currency) ?? 0;

    /// <summary>
    /// 出稿する。利用者が広告費の請求を確認したことが前提（画面で確認する）。各社で審査が終わると配信が始まる。
    /// 失敗したら各社につくった分は消し、理由を残して「失敗」にする（直してもう一度出稿できる）。
    /// </summary>
    public async Task<AdCampaign> SubmitAsync(AdDraft draft, bool chargesConfirmed, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        if (!chargesConfirmed) throw new DomainException(ErrorCodes.Validation, "広告費が請求されることを確認してください。");
        var account = await db.AdAccounts.FirstOrDefaultAsync(a => a.Id == draft.AdAccountId && a.Status != AdAccountStatus.Disconnected, ct)
                      ?? throw new NotFoundException("広告アカウント");
        var adapter = networks.Get(account.Network, account.IsDemo) ?? throw NotAvailable(draft.Platform);
        if (!adapter.Platforms.Contains(draft.Platform)) throw new DomainException(ErrorCodes.Validation, "この広告アカウントでは、この SNS に広告を出せません。");

        var c = PlatformCatalog.Get(draft.Platform);
        var campaign = new AdCampaign
        {
            TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, AdAccountId = account.Id, Network = account.Network,
            Platform = draft.Platform, Objective = draft.Objective, DailyBudget = decimal.Round(draft.DailyBudget, 0), Currency = account.Currency,
            StartAt = draft.StartAt.ToUniversalTime(), EndAt = draft.EndAt.ToUniversalTime(), Targeting = draft.Targeting,
            Creative = draft.Creative with { PrimaryText = draft.Creative.PrimaryText.Trim(), Headline = draft.Creative.Headline.Trim() },
            Name = string.IsNullOrWhiteSpace(draft.Name)
                ? $"{c.DisplayName} {PostText.Truncate(draft.Creative.Headline is { Length: > 0 } h ? h : draft.Creative.PrimaryText, 20)}"
                : draft.Name.Trim(),
            CreatedBy = tenant.UserName,
        };
        var errors = campaign.Validate(clock.GetUtcNow(), adapter.MinDailyBudget(account.Currency)).ToList();
        MediaAsset? asset = null;
        if (campaign.Creative.MediaAssetId is { } mediaId) asset = await media.GetAsync(mediaId, ct);
        if (draft.Platform == SocialPlatform.YouTube)
        {
            if (string.IsNullOrWhiteSpace(campaign.Creative.YouTubeVideoId)) errors.Add("広告にする YouTube の動画を選んでください。");
            if (asset?.Kind != MediaKind.Image) errors.Add("お店・ブランドのロゴ画像（正方形）を選んでください。");
        }
        else if (adapter.RequiresVideo(draft.Platform) && asset?.Kind != MediaKind.Video)
        {
            errors.Add($"{c.DisplayName} の広告には動画が必要です。");
        }
        else if (asset is null)
        {
            errors.Add("広告に使う画像か動画を選んでください。");
        }
        if (errors.Count > 0) throw new DomainException(ErrorCodes.Validation, string.Join(" ", errors));

        db.AdCampaigns.Add(campaign);
        campaign.MarkSubmitting(clock.GetUtcNow());
        db.Record(tenant, "ads.submitting", nameof(AdCampaign), campaign.Id, $"{account.Network} {campaign.DailyBudget} {campaign.Currency}");
        await db.SaveChangesAsync(ct);

        try
        {
            var token = await TokenAsync(account, adapter, ct);
            var submission = new AdSubmission(campaign, account, token, asset is null ? null : await PublishMediaAsync(asset, ct),
                asset is null ? null : await ThumbnailAsync(asset, ct), await PageIdAsync(draft.Platform, ct),
                await SocialAccountIdAsync(SocialPlatform.Instagram, ct), await SocialAccountIdAsync(SocialPlatform.X, ct),
                (await brand.BuildAsync(tenant.WorkspaceId, [], null, ct)).Profile.BrandName);
            var result = await adapter.SubmitAsync(submission, ct);
            campaign.MarkSubmitted(result.ExternalIds, result.Status);
            if (result.AccountExtra is { Count: > 0 } extra)
            {
                account.Extra = new Dictionary<string, string>(account.Extra.Concat(extra.Where(e => !account.Extra.ContainsKey(e.Key))));
            }
            db.Record(tenant, "ads.submitted", nameof(AdCampaign), campaign.Id, result.Status.ToString());
        }
        catch (Exception ex) when (ex is SocialApiException or DomainException)
        {
            campaign.MarkFailed(ex.Message);
            if (ex is SocialApiException { RequiresReauth: true }) account.Status = AdAccountStatus.NeedsReauth;
            db.Record(tenant, "ads.failed", nameof(AdCampaign), campaign.Id, ex.Message);
            log.LogWarning(ex, "Ad submission failed for {CampaignId}", campaign.Id);
        }
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task<AdCampaign> SetPausedAsync(Guid campaignId, bool paused, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var campaign = await db.AdCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct) ?? throw new NotFoundException("広告");
        var account = await db.AdAccounts.FirstAsync(a => a.Id == campaign.AdAccountId, ct);
        var adapter = networks.Get(account.Network, account.IsDemo) ?? throw NotAvailable(campaign.Platform);
        await adapter.SetPausedAsync(campaign, account, await TokenAsync(account, adapter, ct), paused, ct);
        campaign.SetPaused(paused);
        db.Record(tenant, paused ? "ads.paused" : "ads.resumed", nameof(AdCampaign), campaign.Id, null);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    /// <summary>1つの広告の状態・成果を各社から読み直す。</summary>
    public async Task<AdCampaign> SyncAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.AdCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct) ?? throw new NotFoundException("広告");
        await SyncOneAsync(campaign, ct);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    /// <summary>定期処理：配信中・審査中・停止中の広告の状態と成果を読み直す（システムコンテキストで実行）。</summary>
    public async Task<AdRunResult> SyncDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("SyncDueAsync はシステムコンテキストで実行してください。");
        var live = await db.AdCampaigns.Where(c => c.Status == AdStatus.InReview || c.Status == AdStatus.Active || c.Status == AdStatus.Paused)
            .ToListAsync(ct);
        int synced = 0, failed = 0;
        foreach (var campaign in live)
        {
            try
            {
                await SyncOneAsync(campaign, ct);
                synced++;
            }
            catch (SocialApiException ex)
            {
                failed++;
                log.LogWarning(ex, "Ad sync failed for {CampaignId}", campaign.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        return new AdRunResult(synced, failed);
    }

    private async Task SyncOneAsync(AdCampaign campaign, CancellationToken ct)
    {
        var account = await db.AdAccounts.FirstAsync(a => a.Id == campaign.AdAccountId, ct);
        var adapter = networks.Get(account.Network, account.IsDemo) ?? throw NotAvailable(campaign.Platform);
        var state = await adapter.GetStateAsync(campaign, account, await TokenAsync(account, adapter, ct), clock.GetUtcNow(), ct);
        var status = state.Status == AdStatus.Active && clock.GetUtcNow() > campaign.EndAt ? AdStatus.Completed : state.Status;
        campaign.Sync(status, state.Results, state.ReviewNote);
    }

    /// <summary>トークン（期限が近ければ更新）。更新できなければ再連携が必要。</summary>
    private async Task<StoredToken> TokenAsync(AdAccount account, IAdNetworkAdapter adapter, CancellationToken ct)
    {
        var token = await credentials.LoadAsync(account.CredentialSecretRef, ct)
                    ?? throw new SocialApiException(ErrorCodes.SnsReauthRequired, "広告アカウントの再連携が必要です。", false);
        if (token.ExpiresWithin(TimeSpan.FromDays(7), clock.GetUtcNow()))
        {
            try
            {
                token = await adapter.RefreshAsync(token, ct);
                account.CredentialSecretRef = await credentials.SaveAsync(account.TenantId, account.Id, token, account.CredentialSecretRef, ct);
                account.TokenExpiresAt = token.ExpiresAt;
            }
            catch (SocialApiException ex) when (ex.RequiresReauth)
            {
                account.Status = AdAccountStatus.NeedsReauth;
                throw;
            }
        }
        return token;
    }

    /// <summary>Meta の広告に使う Facebook ページ（Facebook の連携、なければ Instagram の連携のページ）。</summary>
    private async Task<string?> PageIdAsync(SocialPlatform platform, CancellationToken ct)
    {
        if (AdNetworks.For(platform) != AdNetwork.Meta) return null;
        var fb = await ChannelAsync(SocialPlatform.Facebook, ct);
        if (fb is not null) return fb.ExternalAccountId;
        var ig = await ChannelAsync(SocialPlatform.Instagram, ct);
        return ig is null || ig.IsDemo ? null : (await channelTokens.GetCredentialAsync(ig, ct)).Token.Get("pageId");
    }

    private async Task<string?> SocialAccountIdAsync(SocialPlatform platform, CancellationToken ct) =>
        (await ChannelAsync(platform, ct))?.ExternalAccountId;

    private async Task<Channel?> ChannelAsync(SocialPlatform platform, CancellationToken ct) =>
        await db.Channels.FirstOrDefaultAsync(c => c.Platform == platform && c.Status == ChannelStatus.Active, ct);

    private async Task<PublishMedia> PublishMediaAsync(MediaAsset asset, CancellationToken ct)
    {
        var bytes = await media.ReadAsync(asset, ct);
        return new PublishMedia(asset.Id, asset.Mime, asset.AltText, asset.IsAiLabeled, _ => Task.FromResult(bytes),
            _ => throw new SocialApiException(ErrorCodes.PubFailed, "広告の画像はアップロードで送ります", false),
            _ => throw new SocialApiException(ErrorCodes.PubFailed, "広告の画像はアップロードで送ります", false), asset.SubtitlesSrt);
    }

    /// <summary>動画のサムネイル（JPEG）。画像ならその画像。</summary>
    private async Task<byte[]?> ThumbnailAsync(MediaAsset asset, CancellationToken ct)
    {
        if (asset.Kind != MediaKind.Video) return null;
        var thumb = await db.MediaAssets.FirstOrDefaultAsync(m => m.ParentAssetId == asset.Id && m.DerivationKey == MediaService.ThumbnailKey, ct);
        return thumb is null ? null : await media.ReadAsync(thumb, ct);
    }

    private static DomainException NotAvailable(SocialPlatform platform) => new(ErrorCodes.Validation,
        AdNetworks.For(platform) is null
            ? $"{PlatformCatalog.Get(platform).DisplayName} の広告は、ReachForge からは出せません（広告 API が一般に公開されていません）。公式の広告の管理画面から出稿してください。"
            : $"{PlatformCatalog.Get(platform).DisplayName} の広告に必要なアプリの設定がありません。「設定 → SNSアプリの設定」で入力してください。");
}
