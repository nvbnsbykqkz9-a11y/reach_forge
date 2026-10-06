using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.X;

/// <summary>
/// X API v2 への投稿（POST /2/tweets）。画像は POST /2/media/upload でアップロードし（media.write スコープ）、
/// ALT テキストを付けてから media_ids で添付する（最大4枚）。URL 付き投稿は費用確認済みのものだけ許可する（PublisherBase）。
/// </summary>
public sealed class XPublisher(IHttpClientFactory http, ILogger<XPublisher>? log = null) : PublisherBase(SocialPlatform.X)
{
    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var client = http.CreateClient(XConnector.HttpClientName);
        var mediaIds = new List<string>();
        foreach (var item in media.Take(Capabilities.MaxImages))
        {
            mediaIds.Add(await UploadAsync(client, item, credential, ct));
        }

        object body = mediaIds.Count == 0
            ? new { text = Text(variant) }
            : new { text = Text(variant), media = new { media_ids = mediaIds } };
        var json = await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "2/tweets", body, credential.AccessToken), "X", ct);
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

    private async Task<string> UploadAsync(HttpClient client, PublishMedia item, ChannelCredential credential, CancellationToken ct)
    {
        var bytes = await item.ReadAsync(ct);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(item.Mime);
        form.Add(file, "media", "image" + (item.Mime == "image/png" ? ".png" : ".jpg"));
        form.Add(new StringContent("tweet_image"), "media_category");
        var request = new HttpRequestMessage(HttpMethod.Post, "2/media/upload") { Content = form };
        request.Headers.Authorization = new("Bearer", credential.AccessToken);
        var mediaId = SocialHttp.Str(await SocialHttp.SendAsync(client, request, "X", ct), "data.id");

        if (!string.IsNullOrWhiteSpace(item.AltText))
        {
            try
            {
                await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "2/media/metadata",
                    new { id = mediaId, metadata = new { alt_text = new { text = item.AltText[..Math.Min(item.AltText.Length, 1000)] } } },
                    credential.AccessToken), "X", ct);
            }
            catch (SocialApiException ex) when (!ex.RequiresReauth)
            {
                // ALT の付与に失敗しても投稿は続ける（画像自体は添付される）
                log?.LogWarning(ex, "Failed to set alt text for X media {MediaId}", mediaId);
            }
        }
        return mediaId;
    }
}
