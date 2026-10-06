using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.X;

/// <summary>X API v2 への投稿（POST /2/tweets）。URL 付き投稿は高額のため、費用確認済みのものだけ許可する（PublisherBase）。</summary>
public sealed class XPublisher(IHttpClientFactory http) : PublisherBase(SocialPlatform.X)
{
    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        CancellationToken ct)
    {
        // TODO(F-04): 画像付き投稿は v2 メディアアップロード（media.write スコープ）後に media_ids を付与する
        var json = await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName),
            SocialHttp.Json(HttpMethod.Post, "2/tweets", new { text = Text(variant) }, credential.AccessToken), "X", ct);
        var id = SocialHttp.Str(json, "data.id");
        var username = credential.Token.Get("username") ?? "i/web";
        return new PublishResult(id, $"https://x.com/{username}/status/{id}");
    }

    public override async Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName),
            new HttpRequestMessage(HttpMethod.Delete, $"2/tweets/{Uri.EscapeDataString(externalPostId)}")
            {
                Headers = { Authorization = new("Bearer", credential.AccessToken) },
            }, "X", ct);
}
