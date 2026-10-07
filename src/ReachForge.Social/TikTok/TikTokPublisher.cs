using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social.TikTok;

/// <summary>
/// TikTok への直接投稿（Content Posting API）。投稿の前に creator_info で公開範囲の選択肢を確認し（TikTok の必須手順）、
/// 動画はファイルを分割アップロード（FILE_UPLOAD → PUT upload_url）、写真は公開 URL から取得させる（PULL_FROM_URL、ドメイン確認が必要）。
/// 投稿は TikTok 側で非同期に処理されるため、status/fetch で公開完了まで確認する。
/// 審査前のアプリの投稿は「自分のみ」に強制される（<see cref="TikTokOptions.Audited"/>、W-SNS-003）。
/// </summary>
public sealed class TikTokPublisher(IHttpClientFactory http, IOptions<SocialOptions> options) : PublisherBase(SocialPlatform.TikTok)
{
    /// <summary>公開範囲（バリアントの PlatformOptions のキー）。値は TikTok の privacy_level。</summary>
    public const string PrivacyOption = PlatformOptionKeys.TikTokPrivacy;
    public const string DisableCommentOption = PlatformOptionKeys.TikTokDisableComment;

    public const string PublicToEveryone = "PUBLIC_TO_EVERYONE";
    public const string SelfOnly = "SELF_ONLY";

    /// <summary>分割アップロードの1回分（TikTok は 5MB〜64MB、最後の1回は残りをまとめて最大128MB）。</summary>
    public const int ChunkBytes = 10 * 1024 * 1024;
    private const int MinChunkBytes = 5 * 1024 * 1024;

    /// <summary>投稿の処理の完了を待つ上限。</summary>
    public static TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>状態確認の待ち時間の倍率（テストで短くする）。</summary>
    public static double PollScale { get; set; } = 1;

    protected override bool RequiresMedia => true;

    public override async Task<PublishValidation> ValidateAsync(PostVariant variant, CancellationToken ct)
    {
        var result = await base.ValidateAsync(variant, ct);
        if (variant.PlatformOptions.TryGetValue(PrivacyOption, out var privacy) && !PlatformOptionKeys.IsValid(PrivacyOption, privacy))
        {
            return new PublishValidation([.. result.Errors, "TikTok の公開範囲が正しくありません"]);
        }
        return result;
    }

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var client = http.CreateClient(TikTokConnector.HttpClientName);
        var creator = (await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "v2/post/publish/creator_info/query/",
            new { }, credential.AccessToken), "TikTok", ct))["data"];
        var privacy = Privacy(variant, creator);
        var disableComment = variant.PlatformOptions.GetValueOrDefault(DisableCommentOption) == "true"
            || creator?["comment_disabled"]?.GetValue<bool>() == true;
        var aigc = media.Any(m => m.IsAiGenerated);

        string publishId;
        if (media.FirstOrDefault(m => m.IsVideo) is { } video)
        {
            var bytes = await video.ReadAsync(ct);
            var chunk = bytes.Length < MinChunkBytes ? bytes.Length : ChunkBytes;
            var count = Math.Max(1, bytes.Length / chunk); // 端数は最後の1回にまとめる
            var init = (await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "v2/post/publish/video/init/", new
            {
                post_info = new
                {
                    title = Text(variant),
                    privacy_level = privacy,
                    disable_comment = disableComment,
                    disable_duet = creator?["duet_disabled"]?.GetValue<bool>() == true,
                    disable_stitch = creator?["stitch_disabled"]?.GetValue<bool>() == true,
                    video_cover_timestamp_ms = 1000,
                    is_aigc = aigc,
                },
                source_info = new { source = "FILE_UPLOAD", video_size = bytes.Length, chunk_size = chunk, total_chunk_count = count },
            }, credential.AccessToken), "TikTok", ct))["data"];
            publishId = SocialHttp.Str(init, "publish_id");
            var uploadUrl = SocialHttp.Str(init, "upload_url");

            for (var i = 0; i < count; i++)
            {
                var start = (long)i * chunk;
                var end = i == count - 1 ? bytes.Length - 1 : start + chunk - 1;
                var content = new ByteArrayContent(bytes, (int)start, (int)(end - start + 1));
                content.Headers.ContentType = new MediaTypeHeaderValue(video.Mime);
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
                await SocialHttp.SendAsync(client, new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content }, "TikTok", ct);
            }
        }
        else
        {
            var urls = new List<string>();
            foreach (var image in media.Take(Capabilities.MaxImages)) urls.Add(await image.PublicUrlAsync(ct));
            var init = (await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "v2/post/publish/content/init/", new
            {
                post_info = new
                {
                    title = Truncate(variant.Title ?? FirstLine(variant.Body), 90),
                    description = Text(variant),
                    privacy_level = privacy,
                    disable_comment = disableComment,
                    auto_add_music = true,
                    is_aigc = aigc,
                },
                source_info = new { source = "PULL_FROM_URL", photo_cover_index = 0, photo_images = urls },
                post_mode = "DIRECT_POST",
                media_type = "PHOTO",
            }, credential.AccessToken), "TikTok", ct))["data"];
            publishId = SocialHttp.Str(init, "publish_id");
        }

        var postId = await WaitAsync(client, publishId, credential, ct);
        var username = credential.Token.Get("username");
        return postId is null
            ? new PublishResult(publishId, username is null ? null : $"https://www.tiktok.com/@{username}")
            : new PublishResult(postId, username is null ? null : $"https://www.tiktok.com/@{username}/video/{postId}");
    }

    /// <summary>公開完了まで待ち、公開された投稿の ID を返す（「自分のみ」の投稿は ID が返らないため null）。</summary>
    private static async Task<string?> WaitAsync(HttpClient client, string publishId, ChannelCredential credential, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ProcessingTimeout;
        while (true)
        {
            var data = (await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "v2/post/publish/status/fetch/",
                new { publish_id = publishId }, credential.AccessToken), "TikTok", ct))["data"];
            switch (SocialHttp.StrOrNull(data, "status"))
            {
                case "PUBLISH_COMPLETE":
                    // 応答のキー名は TikTok の表記（publicaly）のまま
                    return data?["publicaly_available_post_id"] is JsonArray { Count: > 0 } ids ? ids[0]?.ToString() : null;
                case "FAILED":
                    throw new SocialApiException(ErrorCodes.PubFailed,
                        $"TikTok が投稿を処理できませんでした（{SocialHttp.StrOrNull(data, "fail_reason") ?? "理由不明"}）", isTransient: false);
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                // 再試行すると二重投稿になりうるため、恒久的エラーとして利用者に確認してもらう
                throw new SocialApiException(ErrorCodes.PubFailed,
                    "TikTok での処理が終わりませんでした。投稿されている可能性があるため、TikTok アプリで確認してください。", isTransient: false);
            }
            await Task.Delay(TimeSpan.FromSeconds(3) * PollScale, ct);
        }
    }

    /// <summary>
    /// 公開範囲。審査前のアプリは「自分のみ」。指定がなければ全員に公開し、
    /// 指定した範囲をクリエイターが選べない場合（非公開アカウントなど）は選べる範囲のうち最も狭いものにする。
    /// </summary>
    private string Privacy(PostVariant variant, JsonNode? creator)
    {
        if (!options.Value.TikTok.Audited) return SelfOnly;
        var allowed = (creator?["privacy_level_options"] as JsonArray)?.Select(n => n?.ToString()).OfType<string>().ToList() ?? [];
        var wanted = variant.PlatformOptions.GetValueOrDefault(PrivacyOption) ?? PublicToEveryone;
        if (allowed.Count == 0 || allowed.Contains(wanted)) return wanted;
        return allowed.Contains(SelfOnly) ? SelfOnly : allowed[^1];
    }

    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.FromException(new SocialApiException(ErrorCodes.PubFailed,
            "TikTok の投稿は API から削除できません。TikTok アプリから削除してください。", false));

    private static string FirstLine(string body) => body.Split('\n', 2)[0].Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
