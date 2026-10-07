using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.X;

/// <summary>
/// X API v2 への投稿（POST /2/tweets）。画像は POST /2/media/upload でアップロードし（media.write スコープ）、
/// ALT テキストを付けてから media_ids で添付する（最大4枚）。動画（1本）は分割アップロード
/// （initialize → append → finalize → 処理完了の確認）で送る。URL 付き投稿は費用確認済みのものだけ許可する（PublisherBase）。
/// </summary>
public sealed class XPublisher(IHttpClientFactory http, ILogger<XPublisher>? log = null) : PublisherBase(SocialPlatform.X)
{
    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var client = http.CreateClient(XConnector.HttpClientName);
        var mediaIds = new List<string>();
        if (media.FirstOrDefault(m => m.IsVideo) is { } video)
        {
            mediaIds.Add(await UploadVideoAsync(client, video, credential, ct)); // 動画は1本だけ（画像と混ぜない）
        }
        else
        {
            foreach (var item in media.Take(Capabilities.MaxImages))
            {
                mediaIds.Add(await UploadAsync(client, item, credential, ct));
            }
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

    /// <summary>分割アップロードの1回分（X の上限 5MB より小さくする）。</summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>動画の処理の完了を待つ上限。</summary>
    public static TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private async Task<string> UploadVideoAsync(HttpClient client, PublishMedia item, ChannelCredential credential, CancellationToken ct)
    {
        var bytes = await item.ReadAsync(ct);
        var init = await SocialHttp.SendAsync(client, SocialHttp.Json(HttpMethod.Post, "2/media/upload/initialize",
            new { media_type = item.Mime, total_bytes = bytes.Length, media_category = "tweet_video" }, credential.AccessToken), "X", ct);
        var mediaId = SocialHttp.Str(init, "data.id");

        for (var (offset, segment) = (0, 0); offset < bytes.Length; offset += ChunkBytes, segment++)
        {
            using var form = new MultipartFormDataContent();
            var chunk = new ByteArrayContent(bytes, offset, Math.Min(ChunkBytes, bytes.Length - offset));
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(chunk, "media", "chunk");
            form.Add(new StringContent(segment.ToString(System.Globalization.CultureInfo.InvariantCulture)), "segment_index");
            var append = new HttpRequestMessage(HttpMethod.Post, $"2/media/upload/{Uri.EscapeDataString(mediaId)}/append") { Content = form };
            append.Headers.Authorization = new("Bearer", credential.AccessToken);
            await SocialHttp.SendAsync(client, append, "X", ct);
        }

        var finalize = new HttpRequestMessage(HttpMethod.Post, $"2/media/upload/{Uri.EscapeDataString(mediaId)}/finalize");
        finalize.Headers.Authorization = new("Bearer", credential.AccessToken);
        var state = await SocialHttp.SendAsync(client, finalize, "X", ct);

        // 動画は X 側でエンコードされる。処理が終わるまで待つ（状態は check_after_secs ごとに確認する）
        var deadline = DateTimeOffset.UtcNow + ProcessingTimeout;
        while (true)
        {
            var processing = state["data"]?["processing_info"];
            var status = processing?["state"]?.GetValue<string>() ?? "succeeded";
            if (status == "succeeded") break;
            if (status == "failed")
            {
                var message = processing?["error"]?["message"]?.GetValue<string>();
                throw new SocialApiException(Domain.Common.ErrorCodes.PubFailed, $"X が動画を処理できませんでした。{message}", isTransient: false);
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new SocialApiException(Domain.Common.ErrorCodes.PubFailed, "X の動画の処理が終わりませんでした。時間をおいて再試行します。", isTransient: true);
            }
            var wait = processing?["check_after_secs"]?.GetValue<int>() ?? 2;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 10)) * PollScale, ct);
            var check = new HttpRequestMessage(HttpMethod.Get, $"2/media/upload?command=STATUS&media_id={Uri.EscapeDataString(mediaId)}");
            check.Headers.Authorization = new("Bearer", credential.AccessToken);
            state = await SocialHttp.SendAsync(client, check, "X", ct);
        }
        return mediaId;
    }

    /// <summary>状態確認の待ち時間の倍率（テストで短くする）。</summary>
    public static double PollScale { get; set; } = 1;

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
