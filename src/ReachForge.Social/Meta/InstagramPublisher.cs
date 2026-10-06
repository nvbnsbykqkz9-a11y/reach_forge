using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Meta;

/// <summary>
/// Instagram への投稿（Content Publishing：コンテナ作成 → 処理完了待ち → 公開、RF-DES-001 付録 A.3）。
/// 画像は JPEG の公開 URL（短時間 SAS）から取得されるため、メディアの添付が必須。
/// 2枚以上はカルーセル（各画像を is_carousel_item のコンテナにし、media_type=CAROUSEL の親で束ねる。最大10枚）。
/// </summary>
public sealed class InstagramPublisher(IHttpClientFactory http, IOptions<SocialOptions> options)
    : PublisherBase(SocialPlatform.Instagram)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public const int MaxPolls = 30;
    public const int VideoMaxPolls = 150; // 2秒×150＝5分

    private string Version => options.Value.Meta.GraphVersion;
    protected override bool RequiresMedia => true;

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        if (media.Count == 0) throw new SocialApiException(ErrorCodes.PubFailed, "Instagram には画像が必要です", isTransient: false);
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var endpoint = $"{Version}/{credential.ExternalAccountId}/media";

        // ① メディアコンテナ作成（画像は JPEG（sRGB・8MB 以下）に変換済み）
        string container;
        if (media[0].IsVideo)
        {
            // リール（縦型動画）：動画の処理には時間がかかるため、完了待ちを長めにする
            container = await CreateAsync(client, endpoint,
                [new("media_type", "REELS"), new("video_url", await media[0].PublicUrlAsync(ct)), new("caption", Text(variant)),
                 new("share_to_feed", "true")], credential, ct);
            await WaitAsync(client, container, credential, ct, VideoMaxPolls);
        }
        else if (media.Count == 1)
        {
            container = await CreateAsync(client, endpoint,
                [new("image_url", await media[0].PublicUrlAsync(ct)), new("caption", Text(variant))], credential, ct);
        }
        else
        {
            var children = new List<string>();
            foreach (var item in media.Take(Capabilities.MaxImages))
            {
                var child = await CreateAsync(client, endpoint,
                    [new("image_url", await item.PublicUrlAsync(ct)), new("is_carousel_item", "true")], credential, ct);
                await WaitAsync(client, child, credential, ct);
                children.Add(child);
            }
            container = await CreateAsync(client, endpoint,
                [new("media_type", "CAROUSEL"), new("children", string.Join(',', children)), new("caption", Text(variant))], credential, ct);
        }

        // ② 処理完了待ち（status_code=FINISHED）
        await WaitAsync(client, container, credential, ct);

        // ③ 公開
        var mediaId = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{Version}/{credential.ExternalAccountId}/media_publish", [new("creation_id", container)], credential.AccessToken),
            "Instagram", ct), "id");
        var permalink = await SocialHttp.SendAsync(client,
            SocialHttp.Get($"{Version}/{mediaId}?fields=permalink", credential.AccessToken), "Instagram", ct);
        return new PublishResult(mediaId, SocialHttp.StrOrNull(permalink, "permalink"));
    }

    private static async Task<string> CreateAsync(HttpClient client, string endpoint, KeyValuePair<string, string>[] fields,
        ChannelCredential credential, CancellationToken ct) =>
        SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post, endpoint, fields, credential.AccessToken),
            "Instagram", ct), "id");

    private async Task WaitAsync(HttpClient client, string container, ChannelCredential credential, CancellationToken ct, int maxPolls = MaxPolls)
    {
        for (var i = 0; ; i++)
        {
            var status = await SocialHttp.SendAsync(client,
                SocialHttp.Get($"{Version}/{container}?fields=status_code", credential.AccessToken), "Instagram", ct);
            var code = SocialHttp.StrOrNull(status, "status_code");
            if (code is "FINISHED" or null) return;
            if (code is "ERROR" or "EXPIRED")
            {
                throw new SocialApiException(ErrorCodes.PubFailed, $"Instagram がメディアを処理できませんでした（{code}）", isTransient: false);
            }
            if (i >= maxPolls) throw new SocialApiException(SocialHttp.TransientCode, "Instagram のメディア処理が終わりません", isTransient: true);
            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>Instagram Graph API は投稿の削除に対応していない。</summary>
    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.FromException(new SocialApiException(ErrorCodes.PubFailed,
            "Instagram の投稿は API から削除できません。アプリから削除してください。", false));
}
