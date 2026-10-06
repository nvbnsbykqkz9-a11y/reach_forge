using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social;

/// <summary>アダプタ共通の最終検証（配信直前の ValidateAsync）。プラットフォーム制約マスタを参照する。</summary>
public abstract class PublisherBase(SocialPlatform platform) : ISocialPublisher
{
    public SocialPlatform Platform { get; } = platform;
    public PlatformConstraint Capabilities { get; } = PlatformCatalog.Get(platform);
    public virtual bool IsSimulation => false;

    /// <summary>画像・動画が必須の SNS（Instagram など）。</summary>
    protected virtual bool RequiresMedia => false;

    public virtual Task<PublishValidation> ValidateAsync(PostVariant variant, CancellationToken ct)
    {
        var errors = new List<string>();
        var text = PostText.Compose(variant.Body, variant.Hashtags);
        if (string.IsNullOrWhiteSpace(variant.Body)) errors.Add("本文が空です");
        if (PostText.Length(text) > Capabilities.MaxBodyLength) errors.Add($"本文が{Capabilities.MaxBodyLength:N0}字をこえています");
        if (Capabilities.MaxHashtags is { } max && PostText.Hashtags(text).Count > max) errors.Add($"ハッシュタグが{max}個をこえています");
        if (Capabilities.LinkPolicy == LinkPolicy.Required && !PostText.ContainsUrl(text)) errors.Add("遷移先URLがありません");
        if (Capabilities.LinkPolicy == LinkPolicy.DiscouragedByCost && PostText.ContainsUrl(text) && !variant.UrlCostAcknowledged)
        {
            errors.Add("URL付き投稿の費用が確認されていません");
        }
        if (RequiresMedia && !IsSimulation && variant.MediaAssetIds.Count == 0)
        {
            errors.Add($"{Capabilities.DisplayName}には画像または動画が必要です");
        }
        return Task.FromResult(new PublishValidation(errors));
    }

    public abstract Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential, CancellationToken ct);

    public abstract Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct);

    protected static string Text(PostVariant v) => PostText.Compose(v.Body, v.Hashtags);
}
