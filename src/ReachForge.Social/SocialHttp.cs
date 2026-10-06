using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;

namespace ReachForge.Social;

/// <summary>
/// SNS API 呼び出しの共通処理とエラー分類（F-08-5）。
/// 401 → 要再接続（恒久）、429・5xx・通信断・タイムアウト → 一時的（PublishingService が指数バックオフで再試行）、
/// それ以外の 4xx → 恒久的エラー（規約違反・メディア不正など）。
/// 投稿系の POST は HTTP ハンドラでは再試行しない（二重投稿防止：ServiceDefaults で unsafe メソッドの再試行を無効化）。
/// </summary>
public static class SocialHttp
{
    public const string TransientCode = "E-PUB-503";
    public const string ConflictCode = "E-PUB-409";

    public static async Task<JsonNode> SendAsync(HttpClient http, HttpRequestMessage request, string platformName,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SocialApiException(TransientCode, $"{platformName}に接続できませんでした（{ex.Message}）", isTransient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SocialApiException(TransientCode, $"{platformName}の応答がタイムアウトしました", isTransient: true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
            }
            catch (JsonException)
            {
            }

            if (response.IsSuccessStatusCode) return json ?? new JsonObject();

            var detail = ErrorMessage(json) ?? $"HTTP {(int)response.StatusCode}";
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new SocialApiException(ErrorCodes.SnsReauthRequired,
                    $"{platformName}の再認証が必要です（{detail}）", isTransient: false),
                HttpStatusCode.Conflict => new SocialApiException(ConflictCode, detail, isTransient: false),
                HttpStatusCode.TooManyRequests => new SocialApiException(TransientCode,
                    $"{platformName}のAPI利用上限に達しました。時間をおいて再試行します（{detail}）", isTransient: true),
                >= HttpStatusCode.InternalServerError => new SocialApiException(TransientCode,
                    $"{platformName}側が一時的に利用できません（{detail}）", isTransient: true),
                _ when IsMetaTokenError(json) => new SocialApiException(ErrorCodes.SnsReauthRequired,
                    $"{platformName}の再認証が必要です（{detail}）", isTransient: false),
                _ => new SocialApiException(ErrorCodes.PubFailed, detail, isTransient: false),
            };
        }
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, object body, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }

    public static HttpRequestMessage Form(HttpMethod method, string url, IEnumerable<KeyValuePair<string, string>> fields,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(fields) };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }

    public static HttpRequestMessage Get(string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }

    public static string Query(params (string Key, string? Value)[] parameters) =>
        string.Join('&', parameters.Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));

    public static string Str(JsonNode? node, string path) =>
        StrOrNull(node, path)
        ?? throw new SocialApiException(ErrorCodes.PubFailed, $"SNSの応答に {path} がありません", isTransient: false);

    public static string? StrOrNull(JsonNode? node, string path)
    {
        var current = node;
        foreach (var part in path.Split('.'))
        {
            current = current is JsonObject o ? o[part] : null;
        }
        return current switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => null,
        };
    }

    public static DateTimeOffset? ExpiresAt(JsonNode json, DateTimeOffset now) =>
        long.TryParse(StrOrNull(json, "expires_in"), out var seconds) && seconds > 0 ? now.AddSeconds(seconds) : null;

    /// <summary>各社のエラー形式からメッセージを取り出す（X: detail/title, Meta: error.message, LINE: message）。</summary>
    private static string? ErrorMessage(JsonNode? json) =>
        StrOrNull(json, "error.message") ?? StrOrNull(json, "detail") ?? StrOrNull(json, "message")
        ?? StrOrNull(json, "error_description") ?? StrOrNull(json, "title")
        ?? (json?["error"] is JsonValue v ? v.ToString() : null);

    /// <summary>Meta Graph API のトークン失効（OAuthException code 190）。</summary>
    private static bool IsMetaTokenError(JsonNode? json) => StrOrNull(json, "error.code") == "190";
}
