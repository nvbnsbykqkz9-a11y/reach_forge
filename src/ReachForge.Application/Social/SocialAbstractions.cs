using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Social;

/// <summary>SNS 呼び出しに使う資格情報。トークンはログ出力禁止（RF-DES-001 9.1）。</summary>
public sealed record ChannelCredential(Guid ChannelId, SocialPlatform Platform, string ExternalAccountId, string AccessToken)
{
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
}

/// <summary>SNS への投稿（RF-DES-001 5.3）。SNS ごとの差異はアダプタで吸収する。</summary>
public interface ISocialPublisher
{
    SocialPlatform Platform { get; }
    PlatformConstraint Capabilities { get; }
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
    ISocialPublisher Get(SocialPlatform platform);
}

/// <summary>トークンの保管（本番は Key Vault。DB には参照キーのみ保持）。</summary>
public interface ICredentialStore
{
    Task<string> SaveAsync(Guid channelId, string accessToken, CancellationToken ct);
    Task<ChannelCredential> GetAsync(Channel channel, CancellationToken ct);
    Task DeleteAsync(string secretRef, CancellationToken ct);
}

public sealed record ConnectedAccount(string ExternalAccountId, string DisplayName, string? AvatarUrl,
    string AccessToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes);

/// <summary>OAuth 連携（F-01）。state・PKCE の生成と検証、コード→トークン交換を担う。</summary>
public interface IChannelConnector
{
    bool Supports(SocialPlatform platform);

    /// <summary>認可 URL を返す。デモ接続（モック）の場合は null を返し、<see cref="CompleteAsync"/> を直接呼ぶ。</summary>
    Task<string?> BeginAsync(SocialPlatform platform, Guid workspaceId, string callbackUrl, CancellationToken ct);

    Task<ConnectedAccount> CompleteAsync(SocialPlatform platform, string? code, string? state, CancellationToken ct);
}
