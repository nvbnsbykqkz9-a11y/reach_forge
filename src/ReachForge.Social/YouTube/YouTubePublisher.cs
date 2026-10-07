using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Social.YouTube;

/// <summary>
/// YouTube への動画投稿（videos.insert の再開可能アップロード）。縦型・3分以内の動画は YouTube が自動でショートとして扱う。
/// AI で作った動画は status.containsSyntheticMedia で申告し、字幕（SRT）があれば captions.insert で字幕トラックとして付ける。
/// タイトルはバリアントのタイトル（なければ本文の1行目）、説明欄は本文＋ハッシュタグ。
/// </summary>
public sealed class YouTubePublisher(IHttpClientFactory http, IOptions<SocialOptions> options, ILogger<YouTubePublisher>? log = null)
    : PublisherBase(SocialPlatform.YouTube)
{
    /// <summary>公開設定（バリアントの PlatformOptions のキー）。public / unlisted / private。</summary>
    public const string PrivacyOption = PlatformOptionKeys.YouTubePrivacy;

    protected override bool RequiresMedia => true;

    public override async Task<PublishValidation> ValidateAsync(PostVariant variant, CancellationToken ct)
    {
        var errors = (await base.ValidateAsync(variant, ct)).Errors.ToList();
        if (variant.PlatformOptions.TryGetValue(PrivacyOption, out var privacy) && !PlatformOptionKeys.IsValid(PrivacyOption, privacy))
        {
            errors.Add("YouTube の公開設定が正しくありません");
        }
        if (Title(variant).Length > (Capabilities.MaxTitleLength ?? 100)) errors.Add($"タイトルが{Capabilities.MaxTitleLength}字をこえています");
        return new PublishValidation(errors);
    }

    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        IReadOnlyList<PublishMedia> media, CancellationToken ct)
    {
        var video = media.FirstOrDefault(m => m.IsVideo)
            ?? throw new SocialApiException(Domain.Common.ErrorCodes.PubFailed, "YouTube には動画が必要です", isTransient: false);
        var o = options.Value.YouTube;
        var client = http.CreateClient(YouTubeConnector.HttpClientName);
        var bytes = await video.ReadAsync(ct);

        // 1) アップロードの開始：メタデータを送り、アップロード先（Location）を受け取る
        var start = SocialHttp.Json(HttpMethod.Post, "upload/youtube/v3/videos?uploadType=resumable&part=snippet,status", new
        {
            snippet = new
            {
                title = Title(variant),
                description = Clean(Text(variant)),
                tags = variant.Hashtags.Select(t => t.TrimStart('#')).ToArray(),
                categoryId = o.CategoryId,
                defaultLanguage = o.Language,
                defaultAudioLanguage = o.Language,
            },
            status = new
            {
                privacyStatus = variant.PlatformOptions.GetValueOrDefault(PrivacyOption) ?? "public",
                selfDeclaredMadeForKids = false,
                containsSyntheticMedia = video.IsAiGenerated,
            },
        }, credential.AccessToken);
        start.Headers.Add("X-Upload-Content-Type", video.Mime);
        start.Headers.Add("X-Upload-Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Uri? location = null;
        await SocialHttp.SendAsync(client, start, "YouTube", ct, r => location = r.Headers.Location);
        if (location is null)
        {
            throw new SocialApiException(SocialHttp.TransientCode, "YouTube のアップロード先を取得できませんでした", isTransient: true);
        }

        // 2) 動画の本体を送る（応答が動画リソース）
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(video.Mime);
        var upload = new HttpRequestMessage(HttpMethod.Put, location) { Content = content };
        upload.Headers.Authorization = new("Bearer", credential.AccessToken);
        var id = SocialHttp.Str(await SocialHttp.SendAsync(client, upload, "YouTube", ct), "id");

        // 3) 字幕（失敗しても動画は公開済みなので投稿は成功として扱う）
        if (!string.IsNullOrWhiteSpace(video.SubtitlesSrt))
        {
            try
            {
                await UploadCaptionAsync(client, id, video.SubtitlesSrt, o.Language, credential, ct);
            }
            catch (SocialApiException ex) when (!ex.RequiresReauth)
            {
                log?.LogWarning(ex, "Failed to upload captions for YouTube video {VideoId}", id);
            }
        }
        return new PublishResult(id, $"https://www.youtube.com/watch?v={id}");
    }

    private static async Task UploadCaptionAsync(HttpClient client, string videoId, string srt, string language,
        ChannelCredential credential, CancellationToken ct)
    {
        var body = new MultipartContent("related")
        {
            JsonContent.Create(new { snippet = new { videoId, language, name = "字幕", isDraft = false } }),
            new StringContent(srt, Encoding.UTF8, "application/octet-stream"),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "upload/youtube/v3/captions?uploadType=multipart&part=snippet") { Content = body };
        request.Headers.Authorization = new("Bearer", credential.AccessToken);
        await SocialHttp.SendAsync(client, request, "YouTube", ct);
    }

    public override async Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        await SocialHttp.SendAsync(http.CreateClient(YouTubeConnector.HttpClientName),
            new HttpRequestMessage(HttpMethod.Delete, $"youtube/v3/videos?id={Uri.EscapeDataString(externalPostId)}")
            {
                Headers = { Authorization = new("Bearer", credential.AccessToken) },
            }, "YouTube", ct);

    /// <summary>タイトル（指定がなければ本文の1行目）。YouTube はタイトル・説明欄の「&lt;」「&gt;」を受け付けない。</summary>
    public static string Title(PostVariant variant)
    {
        var title = Clean(string.IsNullOrWhiteSpace(variant.Title) ? variant.Body.Split('\n', 2)[0] : variant.Title).Trim();
        return title.Length <= 100 || !string.IsNullOrWhiteSpace(variant.Title) ? title : title[..100];
    }

    private static string Clean(string s) => s.Replace('<', '＜').Replace('>', '＞');
}
