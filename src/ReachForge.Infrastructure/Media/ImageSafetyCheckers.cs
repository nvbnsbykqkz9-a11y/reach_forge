using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Media;

/// <summary>判定サービスが未設定の環境（Local / Dev）。判定せず "not_checked" として記録する。</summary>
public sealed class NotConfiguredImageSafetyChecker : IImageSafetyChecker
{
    public Task<SafetyVerdict> CheckAsync(byte[] image, string mime, CancellationToken ct) => Task.FromResult(SafetyVerdict.NotChecked);
}

/// <summary>
/// Azure AI Content Safety（画像の分析）による有害性判定（RF-DES-001 3.2 / F-04-4）。
/// いずれかの分類の重大度がしきい値以上ならブロックする。判定サービスの障害時は安全側（ブロック）に倒す。
/// </summary>
public sealed class AzureContentSafetyImageChecker(IHttpClientFactory http, IOptions<ContentSafetyOptions> options,
    ILogger<AzureContentSafetyImageChecker> log) : IImageSafetyChecker
{
    public const string HttpClientName = "content-safety";

    public async Task<SafetyVerdict> CheckAsync(byte[] image, string mime, CancellationToken ct)
    {
        var o = options.Value;
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{o.Endpoint!.TrimEnd('/')}/contentsafety/image:analyze?api-version={o.ApiVersion}")
        {
            Content = JsonContent.Create(new { image = new { content = Convert.ToBase64String(image) } }),
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", o.Key);
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
            var flagged = (json?["categoriesAnalysis"]?.AsArray() ?? [])
                .Where(c => (c?["severity"]?.GetValue<int>() ?? 0) >= o.BlockSeverity)
                .Select(c => c!["category"]!.GetValue<string>())
                .ToList();
            return flagged.Count == 0 ? new SafetyVerdict(false, "ok") : new SafetyVerdict(true, string.Join(",", flagged));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Content Safety check failed; blocking image");
            return new SafetyVerdict(true, "check_failed");
        }
    }
}
