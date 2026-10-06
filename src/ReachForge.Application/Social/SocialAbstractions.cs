using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Social;

/// <summary>
/// SNS のトークン一式。アクセストークン・リフレッシュトークンは資格情報ストア（暗号化／Key Vault）にのみ保存し、
/// DB にはその参照キーだけを持つ。ログ出力禁止（RF-DES-001 9.1）。
/// </summary>
public sealed record StoredToken(
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    public string? Get(string key) => Extra?.GetValueOrDefault(key);

    public StoredToken With(string key, string value) =>
        this with { Extra = new Dictionary<string, string>(Extra ?? new Dictionary<string, string>()) { [key] = value } };

    public bool ExpiresWithin(TimeSpan window, DateTimeOffset now) => ExpiresAt is { } e && e - now <= window;

    public override string ToString() => $"StoredToken(***, expires={ExpiresAt:O})";
}

/// <summary>SNS 呼び出しに使う資格情報。</summary>
public sealed record ChannelCredential(Guid ChannelId, SocialPlatform Platform, string ExternalAccountId, StoredToken Token)
{
    public string AccessToken => Token.AccessToken;

    public override string ToString() => $"ChannelCredential({Platform}, {ExternalAccountId}, ***)";
}

public sealed record PublishValidation(IReadOnlyList<string> Errors)
{
    public static readonly PublishValidation Valid = new([]);
    public bool IsValid => Errors.Count == 0;
}

public sealed record PublishResult(string ExternalPostId, string? Url);

/// <summary>SNS API のエラー。一時的（再試行可）か恒久的かを分類する（F-08-5）。</summary>
public sealed class SocialApiException(string errorCode, string message, bool isTransient) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public bool IsTransient { get; } = isTransient;

    /// <summary>トークン失効・権限取り消しなど、利用者の再接続が必要なエラー。</summary>
    public bool RequiresReauth => ErrorCode == ErrorCodes.SnsReauthRequired;
}

/// <summary>SNS への投稿（RF-DES-001 5.3）。SNS ごとの差異はアダプタで吸収する。</summary>
public interface ISocialPublisher
{
    SocialPlatform Platform { get; }
    PlatformConstraint Capabilities { get; }

    /// <summary>モック（実際には投稿しない）か。</summary>
    bool IsSimulation { get; }

    Task<PublishValidation> ValidateAsync(PostVariant variant, CancellationToken ct);
    Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential, CancellationToken ct);
    Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct);
}

public sealed record PostMetricSnapshot(string ExternalPostId, long Impressions, long Reach, long Views, int Likes,
    int Comments, int Shares, int Saves, int LinkClicks);

public sealed record AccountMetricSnapshot(long Followers, long Impressions, long ProfileVisits);

public interface ISocialInsightsReader
{
    SocialPlatform Platform { get; }
    Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IEnumerable<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct);
    Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly from, DateOnly to, ChannelCredential credential,
        CancellationToken ct);
}

public sealed record InboxItem(string ExternalId, SocialPlatform Platform, string Author, string Text,
    DateTimeOffset ReceivedAt, string? InReplyToExternalPostId);

public interface ISocialInboxReader
{
    SocialPlatform Platform { get; }
    Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, ChannelCredential credential, CancellationToken ct);
    Task ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct);
}

public interface IPublisherFactory
{
    /// <summary>チャネルに対応する投稿アダプタ。デモ接続のチャネルにはモックを返す。</summary>
    ISocialPublisher Get(SocialPlatform platform, bool demo);
}

/// <summary>トークンの保管（本番は Key Vault、それ以外は DB に暗号化保存）。DB には参照キーのみ保持する。</summary>
public interface ICredentialStore
{
    /// <summary>保存して参照キーを返す。<paramref name="existingRef"/> があれば上書きする。</summary>
    Task<string> SaveAsync(Guid tenantId, Guid channelId, StoredToken token, string? existingRef, CancellationToken ct);
    Task<StoredToken?> LoadAsync(string secretRef, CancellationToken ct);
    Task DeleteAsync(string secretRef, CancellationToken ct);
}

/// <summary>連携で取得したアカウント。Meta のように1回の認可で複数のページ・アカウントが返る場合がある。</summary>
public sealed record ConnectedAccount(
    SocialPlatform Platform,
    string ExternalAccountId,
    string DisplayName,
    string? AvatarUrl,
    StoredToken Token,
    IReadOnlyList<string> Scopes,
    bool IsDemo = false);

public enum ConnectMode
{
    /// <summary>OAuth 2.0（認可コード）。SNS の認可画面へリダイレクトする。</summary>
    OAuth,
    /// <summary>資格情報の入力（LINE 公式アカウント：チャネル ID・シークレット）。</summary>
    Credentials,
    /// <summary>デモ接続（モック）。</summary>
    Demo,
}

/// <summary>資格情報入力型の連携で入力してもらう項目。</summary>
public sealed record CredentialField(string Key, string Label, bool Secret, bool Required, string? Help = null);

/// <summary>SNS 連携（F-01）。プラットフォーム別に実装する。</summary>
public interface IChannelConnector
{
    bool Supports(SocialPlatform platform);
    ConnectMode Mode { get; }

    /// <summary>OAuth の認可 URL（state・PKCE の code_challenge を含める）。</summary>
    string BuildAuthorizationUrl(SocialPlatform platform, string state, string codeChallenge, string redirectUri) =>
        throw new NotSupportedException();

    /// <summary>認可コードをトークンへ交換し、連携可能なアカウントを返す。</summary>
    Task<IReadOnlyList<ConnectedAccount>> ExchangeAsync(SocialPlatform platform, string code, string codeVerifier,
        string redirectUri, CancellationToken ct) => throw new NotSupportedException();

    IReadOnlyList<CredentialField> CredentialFields => [];

    /// <summary>資格情報入力型（LINE）またはデモの連携。</summary>
    Task<IReadOnlyList<ConnectedAccount>> ConnectAsync(SocialPlatform platform, IReadOnlyDictionary<string, string> fields,
        CancellationToken ct) => throw new NotSupportedException();

    /// <summary>トークンを更新する。更新できない（再認可が必要）場合は RequiresReauth の SocialApiException を投げる。</summary>
    Task<StoredToken> RefreshAsync(SocialPlatform platform, StoredToken token, CancellationToken ct) => Task.FromResult(token);
}

/// <summary>OAuth の state・PKCE と、アカウント選択待ちの一時保管（10分）。</summary>
public interface IOAuthStateStore
{
    Task SaveAsync(string state, PendingAuthorization pending, CancellationToken ct);

    /// <summary>取り出して削除する（state は1回限り有効）。</summary>
    Task<PendingAuthorization?> TakeAsync(string state, CancellationToken ct);

    Task SaveSelectionAsync(string key, PendingSelection selection, CancellationToken ct);
    Task<PendingSelection?> GetSelectionAsync(string key, CancellationToken ct);
    Task RemoveSelectionAsync(string key, CancellationToken ct);
}

public sealed record PendingAuthorization(Guid TenantId, Guid WorkspaceId, string UserName, SocialPlatform Platform,
    string CodeVerifier, string RedirectUri);

public sealed record PendingSelection(Guid TenantId, Guid WorkspaceId, string UserName, SocialPlatform Platform,
    IReadOnlyList<ConnectedAccount> Accounts);

/// <summary>メディアの短時間・読取専用 URL（SAS）。Instagram などは公開 URL から画像を取得する。</summary>
public interface IMediaUrlSigner
{
    Task<string> CreateReadUrlAsync(Guid mediaAssetId, TimeSpan lifetime, CancellationToken ct);
}
