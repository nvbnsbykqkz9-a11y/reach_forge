using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Threads;

/// <summary>Threads への投稿（コンテナ作成 POST /{user-id}/threads → 公開 POST /{user-id}/threads_publish）。</summary>
public sealed class ThreadsPublisher(IHttpClientFactory http, IOptions<SocialOptions> options) : PublisherBase(SocialPlatform.Threads)
{
    private string Version => options.Value.Threads.ApiVersion;

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var client = http.CreateClient(ThreadsConnector.HttpClientName);
        var fields = new List<KeyValuePair<string, string>> { new("text", Text(variant)) };
        if (media.FirstOrDefault() is { } image)
        {
            fields.Add(new("media_type", "IMAGE"));
            fields.Add(new("image_url", await image.PublicUrlAsync(ct)));
        }
        else
        {
            fields.Add(new("media_type", "TEXT"));
        }
        var container = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{Version}/{credential.ExternalAccountId}/threads", fields, credential.AccessToken), "Threads", ct), "id");

        var id = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{Version}/{credential.ExternalAccountId}/threads_publish", [new("creation_id", container)], credential.AccessToken),
            "Threads", ct), "id");

        var permalink = await SocialHttp.SendAsync(client,
            SocialHttp.Get($"{Version}/{id}?fields=permalink", credential.AccessToken), "Threads", ct);
        return new PublishResult(id, SocialHttp.StrOrNull(permalink, "permalink"));
    }

    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.FromException(new SocialApiException(ErrorCodes.PubFailed,
            "Threads の投稿は API から削除できません。アプリから削除してください。", false));
}
