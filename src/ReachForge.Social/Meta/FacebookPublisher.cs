using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social.Meta;

/// <summary>Facebook ページへの投稿（POST /{page-id}/feed）。個人プロフィールには投稿できない。</summary>
public sealed class FacebookPublisher(IHttpClientFactory http, IOptions<SocialOptions> options) : PublisherBase(SocialPlatform.Facebook)
{
    private string Version => options.Value.Meta.GraphVersion;

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var text = Text(variant);
        if (media.FirstOrDefault() is { } image)
        {
            // 写真投稿（POST /{page-id}/photos）：url から取得、message がキャプション
            var photo = await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
                $"{Version}/{credential.ExternalAccountId}/photos",
                [new("url", await image.PublicUrlAsync(ct)), new("message", text)], credential.AccessToken), "Facebook", ct);
            var postId = SocialHttp.StrOrNull(photo, "post_id") ?? SocialHttp.Str(photo, "id");
            return new PublishResult(postId, $"https://www.facebook.com/{postId}");
        }

        var fields = new List<KeyValuePair<string, string>> { new("message", text) };
        if (PostText.Urls(text).FirstOrDefault() is { } link) fields.Add(new("link", link)); // OGP カードを表示
        var json = await SocialHttp.SendAsync(client,
            SocialHttp.Form(HttpMethod.Post, $"{Version}/{credential.ExternalAccountId}/feed", fields, credential.AccessToken),
            "Facebook", ct);
        var id = SocialHttp.Str(json, "id");
        return new PublishResult(id, $"https://www.facebook.com/{id}");
    }

    public override async Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName),
            new HttpRequestMessage(HttpMethod.Delete, $"{Version}/{Uri.EscapeDataString(externalPostId)}")
            {
                Headers = { Authorization = new("Bearer", credential.AccessToken) },
            }, "Facebook", ct);
}
