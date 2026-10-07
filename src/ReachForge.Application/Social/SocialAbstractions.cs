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

/// <summary>
/// 投稿に添付する画像（SNS 別の比率・形式に変換済み）。SNS が URL から取得する場合は公開 URL（短時間 SAS）、
/// アップロードする場合（X・TikTok・YouTube）はバイト列を使う。動画の字幕（SRT）は字幕トラックとして送れる SNS（YouTube）で使う。
/// </summary>
public sealed record PublishMedia(
    Guid AssetId,
    string Mime,
    string? AltText,
    bool IsAiGenerated,
    Func<CancellationToken, Task<byte[]>> ReadAsync,
    Func<CancellationToken, Task<string>> PublicUrlAsync,
    Func<CancellationToken, Task<string>> PreviewUrlAsync,
    string? SubtitlesSrt = null)
{
    public bool IsVideo => Mime.StartsWith("video/", StringComparison.Ordinal);
}

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
    Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential, IReadOnlyList<PublishMedia> media,
        CancellationToken ct);
    Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct);
}

/// <summary>
/// 投稿1件の指標。SNS が提供しない値は 0（表示回数を提供しない SNS はリーチ・再生数で代替：MetricsCalculator）。
/// </summary>
public sealed record PostMetricSnapshot(string ExternalPostId, long Impressions, long Reach, long Views, int Likes,
    int Comments, int Shares, int Saves, int LinkClicks, int ProfileVisits = 0, int Follows = 0);

public sealed record AccountMetricSnapshot(long Followers, long Impressions, long ProfileVisits);

/// <summary>SNS の指標取得（F-10 MetricsCollectJob）。</summary>
public interface ISocialInsightsReader
{
    SocialPlatform Platform { get; }

    /// <summary>モック（デモ接続用の擬似値）か。</summary>
    bool IsSimulation { get; }

    /// <summary>投稿ごとの指標。取得できなかった投稿（削除済みなど）は結果に含めない。</summary>
    Task<IReadOnlyList<PostMetricSnapshot>> GetPostMetricsAsync(IReadOnlyList<string> externalPostIds,
        ChannelCredential credential, CancellationToken ct);

    /// <summary>アカウントの指標（フォロワー数は取得時点の値）。</summary>
    Task<AccountMetricSnapshot> GetAccountMetricsAsync(DateOnly date, ChannelCredential credential, CancellationToken ct);
}

public interface IInsightsReaderFactory
{
    /// <summary>指標取得のアダプタ。SNS が指標 API を提供しない場合は null。</summary>
    ISocialInsightsReader? Get(SocialPlatform platform, bool demo);
}

/// <summary>SNS から取り込んだコメント・メンション・DM。</summary>
public sealed record InboxItem(
    string ExternalId,
    SocialPlatform Platform,
    InboxKind Kind,
    string AuthorId,
    string AuthorName,
    string Text,
    DateTimeOffset ReceivedAt,
    string? InReplyToExternalPostId);

/// <summary>受信箱（F-09）：コメント等の取得・返信・非表示。SNS ごとの差異はアダプタで吸収する。</summary>
public interface ISocialInboxReader
{
    SocialPlatform Platform { get; }
    bool IsSimulation { get; }

    /// <summary>
    /// <paramref name="since"/> 以降のコメント等を取得する（Webhook 非対応・取りこぼし対策のポーリング）。
    /// コメントを投稿単位で取得する SNS（Facebook / Instagram / Threads）には、直近に公開した投稿の ID を渡す。
    /// </summary>
    Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct);

    /// <summary>返信して、SNS 上の返信の ID を返す。</summary>
    Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct);

    /// <summary>コメントを非表示にする（スパム対策）。対応していない SNS は false。</summary>
    Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct) => Task.FromResult(false);
}

public interface IInboxReaderFactory
{
    ISocialInboxReader? Get(SocialPlatform platform, bool demo);
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
