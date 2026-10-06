using System.Security.Cryptography;
using System.Text;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Line;

/// <summary>
/// LINE 公式アカウントからの一斉配信（POST /v2/bot/message/broadcast）。友だち登録（オプトイン）済みユーザーのみが対象。
/// 再試行時の二重配信を防ぐため、投稿バリアントと予約日時から決まる UUID を X-Line-Retry-Key に指定する。
/// 同じキーで受付済みの場合は 409 が返るため、成功として扱う。
/// </summary>
public sealed class LinePublisher(IHttpClientFactory http) : PublisherBase(SocialPlatform.Line)
{
    public override async Task<PublishResult> PublishAsync(PostVariant variant, ChannelCredential credential,
        CancellationToken ct)
    {
        var retryKey = RetryKey(variant);
        var request = SocialHttp.Json(HttpMethod.Post, "v2/bot/message/broadcast",
            new { messages = new[] { new { type = "text", text = Text(variant) } } }, credential.AccessToken);
        request.Headers.Add("X-Line-Retry-Key", retryKey);
        try
        {
            await SocialHttp.SendAsync(http.CreateClient(LineConnector.HttpClientName), request, "LINE", ct);
        }
        catch (SocialApiException ex) when (ex.ErrorCode == SocialHttp.ConflictCode)
        {
            // 同じリトライキーで受付済み（前回の試行が LINE 側では成功していた）
        }
        return new PublishResult(retryKey, null);
    }

    public override Task DeleteAsync(string externalPostId, ChannelCredential credential, CancellationToken ct) =>
        Task.FromException(new SocialApiException(ErrorCodes.PubFailed, "LINE の配信は取り消せません。", false));

    /// <summary>冪等キー（variant_id＋scheduled_at）から決定的に UUID を作る。</summary>
    internal static string RetryKey(PostVariant variant)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(variant.IdempotencyKey));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5 相当
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }
}
