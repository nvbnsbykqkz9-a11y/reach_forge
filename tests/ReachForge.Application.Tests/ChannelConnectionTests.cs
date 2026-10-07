using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Tests;

/// <summary>OAuth の state・PKCE、アカウント選択、資格情報の暗号化保存、トークン更新・要再接続（F-01）。</summary>
public class ChannelConnectionTests
{
    /// <summary>Facebook を OAuth で連携するテスト用コネクタ（2ページを返す）。</summary>
    private sealed class FakeOAuthConnector : IChannelConnector
    {
        public string? LastVerifier;
        public int RefreshCalls;
        public bool FailRefresh;

        public bool Supports(SocialPlatform platform) => platform is SocialPlatform.Facebook or SocialPlatform.Threads;
        public ConnectMode Mode => ConnectMode.OAuth;

        public string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
            $"https://auth.example/authorize?state={state}&code_challenge={codeChallenge}";

        public Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code, string codeVerifier,
            string redirectUri, CancellationToken ct)
        {
            LastVerifier = codeVerifier;
            IReadOnlyList<ConnectedAccount> accounts = platform == SocialPlatform.Facebook
                ?
                [
                    new(SocialPlatform.Facebook, "page-1", "ほっこりカフェ", null, new StoredToken("page-token-1"), ["pages_manage_posts"]),
                    new(SocialPlatform.Facebook, "page-2", "ほっこりカフェ 2号店", null, new StoredToken("page-token-2"), ["pages_manage_posts"]),
                ]
                :
                [
                    new(SocialPlatform.Threads, "th-1", "@hokkori", null,
                        new StoredToken("short-lived", null, new DateTimeOffset(2026, 10, 6, 0, 2, 0, TimeSpan.Zero)), ["threads_basic"]),
                ];
            return Task.FromResult(accounts);
        }

        public Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct)
        {
            RefreshCalls++;
            if (FailRefresh) throw new SocialApiException(ErrorCodes.SnsReauthRequired, "revoked", false);
            return Task.FromResult(token with { AccessToken = "refreshed", ExpiresAt = DateTimeOffset.UtcNow.AddDays(60) });
        }
    }

    private static async Task<(AppFixture F, FakeOAuthConnector Connector)> CreateAsync()
    {
        var connector = new FakeOAuthConnector();
        var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IChannelConnector>(connector), prependConnector: true);
        return (f, connector);
    }

    private static string StateOf(string url) => url.Split("state=")[1].Split('&')[0];

    [Fact]
    public void Pkce_challenge_matches_rfc7636_example() =>
        // RFC 7636 Appendix B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ChannelService.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public async Task OAuth_flow_with_account_selection_stores_encrypted_token()
    {
        var (f, connector) = await CreateAsync();
        await using var _ = f;
        await using var scope = f.Scope();
        var channels = f.Get<ChannelService>(scope);

        var start = await channels.BeginConnectAsync(SocialPlatform.Facebook, "https://app/cb", CancellationToken.None);
        Assert.Equal(ConnectMode.OAuth, start.Mode);
        var state = StateOf(start.AuthorizationUrl!);

        var outcome = await channels.CompleteOAuthAsync(SocialPlatform.Facebook, "code", state, null, CancellationToken.None);
        Assert.Null(outcome.Channel);
        Assert.NotNull(outcome.SelectionKey);
        Assert.Equal(ChannelService.CodeChallenge(connector.LastVerifier!), start.AuthorizationUrl!.Split("code_challenge=")[1]);

        var selection = await channels.GetSelectionAsync(outcome.SelectionKey!, CancellationToken.None);
        Assert.Equal(2, selection!.Accounts.Count);
        var channel = await channels.SelectAccountAsync(outcome.SelectionKey!, "page-2", CancellationToken.None);

        Assert.Equal("ほっこりカフェ 2号店", channel.DisplayName);
        Assert.False(channel.IsDemo);
        Assert.StartsWith("db://", channel.CredentialSecretRef);

        // DB には平文のトークンを保存しない
        var db = f.Get<IAppDbContext>(scope);
        var raw = await ((DbContext)db).Database.SqlQueryRaw<string>("SELECT \"Protected\" AS \"Value\" FROM \"ChannelSecrets\"").ToListAsync();
        Assert.DoesNotContain(raw, r => r.Contains("page-token-2"));

        var credential = await f.Get<ChannelTokenService>(scope).GetCredentialAsync(channel, CancellationToken.None);
        Assert.Equal("page-token-2", credential.AccessToken);
    }

    [Fact]
    public async Task State_is_single_use_and_bound_to_the_user()
    {
        var (f, _) = await CreateAsync();
        await using var __ = f;
        string state;
        await using (var scope = f.Scope())
        {
            state = StateOf((await f.Get<ChannelService>(scope).BeginConnectAsync(SocialPlatform.Facebook, "https://app/cb",
                CancellationToken.None)).AuthorizationUrl!);
        }

        // 別の利用者のコールバックでは使えない（CSRF 対策）
        await using (var other = f.Scope(c => c.UserName = "攻撃者"))
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                f.Get<ChannelService>(other).CompleteOAuthAsync(SocialPlatform.Facebook, "code", state, null, CancellationToken.None));
            Assert.Equal(ErrorCodes.SnsAuthCanceled, ex.ErrorCode);
        }
        // 一度取り出した state は再利用できない
        await using (var scope = f.Scope())
        {
            await Assert.ThrowsAsync<DomainException>(() =>
                f.Get<ChannelService>(scope).CompleteOAuthAsync(SocialPlatform.Facebook, "code", state, null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task User_cancellation_maps_to_E_SNS_001()
    {
        var (f, _) = await CreateAsync();
        await using var __ = f;
        await using var scope = f.Scope();
        var channels = f.Get<ChannelService>(scope);
        var state = StateOf((await channels.BeginConnectAsync(SocialPlatform.Facebook, "https://app/cb", CancellationToken.None)).AuthorizationUrl!);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            channels.CompleteOAuthAsync(SocialPlatform.Facebook, null, state, "access_denied", CancellationToken.None));
        Assert.Equal(ErrorCodes.SnsAuthCanceled, ex.ErrorCode);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_before_use_and_failure_requires_reconnect()
    {
        var (f, connector) = await CreateAsync();
        await using var __ = f;
        await using var scope = f.Scope();
        var channels = f.Get<ChannelService>(scope);
        var state = StateOf((await channels.BeginConnectAsync(SocialPlatform.Threads, "https://app/cb", CancellationToken.None)).AuthorizationUrl!);
        var channel = (await channels.CompleteOAuthAsync(SocialPlatform.Threads, "code", state, null, CancellationToken.None)).Channel!;

        var tokens = f.Get<ChannelTokenService>(scope);
        var credential = await tokens.GetCredentialAsync(channel, CancellationToken.None);
        Assert.Equal("refreshed", credential.AccessToken);
        Assert.Equal(1, connector.RefreshCalls);

        // 期限が近づいたトークンの更新に失敗 → 要再接続
        connector.FailRefresh = true;
        var db = f.Get<IAppDbContext>(scope);
        var secretRef = channel.CredentialSecretRef;
        var store = f.Get<ICredentialStore>(scope);
        await store.SaveAsync(channel.TenantId, channel.Id, new StoredToken("old", null, f.Clock.GetUtcNow().AddDays(1)), secretRef,
            CancellationToken.None);
        channel.TokenExpiresAt = f.Clock.GetUtcNow().AddDays(1);
        await db.SaveChangesAsync();

        await using var worker = f.Scope(c => c.IsSystem = true);
        var (refreshed, reauth) = await f.Get<ChannelTokenService>(worker).RefreshExpiringAsync(CancellationToken.None);
        Assert.Equal((0, 1), (refreshed, reauth));
        var reloaded = await f.Get<IAppDbContext>(worker).Channels.SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(ChannelStatus.ReauthRequired, reloaded.Status);
    }

    [Fact]
    public async Task Disconnect_deletes_stored_secret()
    {
        var (f, _) = await CreateAsync();
        await using var __ = f;
        await using var scope = f.Scope();
        var channels = f.Get<ChannelService>(scope);
        var state = StateOf((await channels.BeginConnectAsync(SocialPlatform.Threads, "https://app/cb", CancellationToken.None)).AuthorizationUrl!);
        var channel = (await channels.CompleteOAuthAsync(SocialPlatform.Threads, "code", state, null, CancellationToken.None)).Channel!;

        await channels.DisconnectAsync(channel.Id, CancellationToken.None);

        Assert.Null(await f.Get<ICredentialStore>(scope).LoadAsync(channel.CredentialSecretRef, CancellationToken.None));
    }

    [Fact]
    public async Task Members_can_be_invited_and_last_owner_is_protected()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var members = f.Get<MemberService>(scope);
        var link = await members.InviteAsync("new@example.com", Role.Approver, CancellationToken.None);
        Assert.Equal(Invitation.Hash(link.Token), link.Invitation.TokenHash);
        Assert.Single(await members.PendingInvitationsAsync(CancellationToken.None));

        await using var editor = f.Scope(c => c.Role = Role.Editor);
        await Assert.ThrowsAsync<Security.ForbiddenException>(() =>
            f.Get<MemberService>(editor).InviteAsync("x@example.com", Role.Editor, CancellationToken.None));

        var db = f.Get<IAppDbContext>(scope);
        var owner = new WorkspaceMember { WorkspaceId = Infrastructure.Persistence.DemoSeeder.WorkspaceId, UserId = Guid.NewGuid(), Email = "o@example.com", Role = Role.Owner };
        db.WorkspaceMembers.Add(owner);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(() => members.ChangeRoleAsync(owner.Id, Role.Editor, CancellationToken.None));
    }
}
