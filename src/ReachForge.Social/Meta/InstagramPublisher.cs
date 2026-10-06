using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Meta;

/// <summary>
/// Instagram への投稿（Content Publishing：コンテナ作成 → 処理完了待ち → 公開、RF-DES-001 付録 A.3）。
/// 画像は JPEG の公開 URL（短時間 SAS）から取得されるため、メディアの添付が必須。
/// </summary>
public sealed class InstagramPublisher(IHttpClientFactory http, IOptions<SocialOptions> options)
    : PublisherBase(SocialPlatform.Instagram)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public const int MaxPolls = 30;

    private string Version => options.Value.Meta.GraphVersion;
    protected override bool RequiresMedia => true;

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var image = media.FirstOrDefault()
                    ?? throw new SocialApiException(ErrorCodes.PubFailed, "Instagram には画像が必要です", isTransient: false);
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var imageUrl = await image.PublicUrlAsync(ct); // JPEG（sRGB・8MB 以下）に変換済み

        // ① メディアコンテナ作成
        var container = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{Version}/{credential.ExternalAccountId}/media",
            [new("image_url", imageUrl), new("caption", Text(variant))], credential.AccessToken), "Instagram", ct), "id");

        // ② 処理完了待ち（status_code=FINISHED）
        for (var i = 0; ; i++)
        {
            var status = await SocialHttp.SendAsync(client,
                SocialHttp.Get($"{Version}/{container}?fields=status_code", credential.AccessToken), "Instagram", ct);
            var code = SocialHttp.StrOrNull(status, "status_code");
            if (code is "FINISHED" or null) break;
            if (code is "ERROR" or "EXPIRED")
            {
                throw new SocialApiException(ErrorCodes.PubFailed, $"Instagram がメディアを処理できませんでした（{code}）", isTransient: false);
            }
            if (i >= MaxPolls) throw new SocialApiException(SocialHttp.TransientCode, "Instagram のメディア処理が終わりません", isTransient: true);
            await Task.Delay(PollInterval, ct);
        }

        // ③ 公開
        var mediaId = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{Version}/{credential.ExternalAccountId}/media_publish", [new("creation_id", container)], credential.AccessToken),
            "Instagram", ct), "id");
        var permalink = await SocialHttp.SendAsync(client,
            SocialHttp.Get($"{Version}/{mediaId}?fields=permalink", credential.AccessToken), "Instagram", ct);
        return new PublishResult(mediaId, SocialHttp.StrOrNull(permalink, "permalink"));
    }

    /// <summary>Instagram Graph API は投稿の削除に対応していない。</summary>
    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.FromException(new SocialApiException(ErrorCodes.PubFailed,
            "Instagram の投稿は API から削除できません。アプリから削除してください。", false));
}
