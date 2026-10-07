using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ReachForge.AI;

public sealed record AiKeyCheckResult(bool Ok, string Message);

/// <summary>
/// 生成 AI の API キーが使えるかを確かめる（各社のモデル一覧の取得。生成はしないので利用料はかからない）。
/// </summary>
public sealed class AiKeyChecker(IHttpClientFactory http, IOptions<AiOptions> options)
{
    public const string HttpClientName = "ai-key-check";

    public async Task<AiKeyCheckResult> CheckAsync(string providerName, CancellationToken ct)
    {
        if (!options.Value.Providers.TryGetValue(providerName, out var provider))
        {
            return new(false, "このプロバイダは設定にありません。");
        }
        if (string.IsNullOrWhiteSpace(provider.ApiKey)) return new(false, "API キーが設定されていません。");

        using var request = provider.Type switch
        {
            AiProviderType.Anthropic => new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1")
            {
                Headers = { { "x-api-key", provider.ApiKey }, { "anthropic-version", "2023-06-01" } },
            },
            AiProviderType.OpenAI => new HttpRequestMessage(HttpMethod.Get, $"{(provider.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/')}/models")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey) },
            },
            AiProviderType.Google => new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1")
            {
                Headers = { { "x-goog-api-key", provider.ApiKey } },
            },
            _ => null,
        };
        if (request is null) return new(true, "確認の必要はありません。");
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new(true, "API キーを確認できました。"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(false, "API キーが正しくないか、権限がありません。"),
                HttpStatusCode.TooManyRequests => new(true, "API キーは有効です（利用上限に近づいています）。"),
                _ => new(false, $"確認できませんでした（HTTP {(int)response.StatusCode}）。時間をおいて試してください。"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(false, "接続できませんでした。インターネットへの接続を確認してください。");
        }
    }

    /// <summary>API キーの指紋（クライアントの再利用の判定に使う。キーそのものは持たない）。</summary>
    public static string Fingerprint(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)), 0, 8);
}
