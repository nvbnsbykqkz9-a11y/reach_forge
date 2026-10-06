using Microsoft.Extensions.Logging;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Mock;

/// <summary>
/// デモ接続チャネル用のモック投稿（RF-DES-001 3.5「SNS はモック」）。実際には投稿せず、成功を返す。
/// 本文に <c>[[transient]]</c> を含めると一時的エラー、<c>[[fail]]</c> を含めると恒久的エラーを再現できる。
/// </summary>
public sealed class MockPublisher(SocialPlatform platform, ILogger<MockPublisher> log, TimeProvider? clock = null)
    : PublisherBase(platform)
{
    public const string TransientMarker = "[[transient]]";
    public const string FailMarker = "[[fail]]";

    public override bool IsSimulation => true;

    public override Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        if (variant.Body.Contains(FailMarker, StringComparison.Ordinal))
        {
            throw new SocialApiException("E-PUB-010", "メディア形式が不正です（モック）", isTransient: false);
        }
        if (variant.Body.Contains(TransientMarker, StringComparison.Ordinal))
        {
            throw new SocialApiException(SocialHttp.TransientCode, "SNS側が一時的に混み合っています（モック）", isTransient: true);
        }

        var id = $"mock-{Platform.ToString().ToLowerInvariant()}-{Guid.CreateVersion7((clock ?? TimeProvider.System).GetUtcNow()):N}";
        log.LogInformation("[Mock] Published {Platform} post {ExternalId} with {Images} image(s) for channel {ChannelId}",
            Platform, id, media.Count, credential.ChannelId);
        return Task.FromResult(new PublishResult(id, $"https://example.invalid/{Platform.ToString().ToLowerInvariant()}/{id}"));
    }

    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.CompletedTask;
}
