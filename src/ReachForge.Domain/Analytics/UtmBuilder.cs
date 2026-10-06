using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Analytics;

/// <summary>
/// UTM パラメータの付与（F-06-4）：utm_source=SNS名, utm_medium=social, utm_campaign=キャンペーンコード。
/// 既に UTM が付いている URL は変更しない。
/// </summary>
public static class UtmBuilder
{
    public static string Append(string url, SocialPlatform platform, string? campaignCode)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Query.Contains("utm_", StringComparison.OrdinalIgnoreCase)) return url;

        var parameters = new List<string>
        {
            $"utm_source={platform.ToString().ToLowerInvariant()}",
            "utm_medium=social",
        };
        if (!string.IsNullOrWhiteSpace(campaignCode))
        {
            parameters.Add($"utm_campaign={Uri.EscapeDataString(campaignCode)}");
        }

        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing) ? string.Join('&', parameters) : $"{existing}&{string.Join('&', parameters)}";
        return builder.Uri.ToString();
    }
}
