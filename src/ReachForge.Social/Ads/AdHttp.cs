using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Ads;

/// <summary>広告 API の共通処理。</summary>
internal static class AdHttp
{
    /// <summary>小数のない通貨（予算をそのままの単位で送る）。</summary>
    private static readonly HashSet<string> ZeroDecimal = ["JPY", "KRW", "VND", "CLP", "ISK", "HUF", "TWD", "COP", "IDR", "PYG"];

    /// <summary>最小単位（円・セント）に直した金額。</summary>
    public static long MinorUnits(decimal amount, string currency) =>
        (long)decimal.Round(ZeroDecimal.Contains(currency.ToUpperInvariant()) ? amount : amount * 100, 0);

    /// <summary>100万分の1単位（X・Google）。</summary>
    public static long Micros(decimal amount) => (long)decimal.Round(amount * 1_000_000m, 0);

    public static decimal FromMicros(long micros) => micros / 1_000_000m;

    public static long Long(JsonNode? node, string path) =>
        long.TryParse(SocialHttp.StrOrNull(node, path), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static decimal Dec(JsonNode? node, string path) =>
        decimal.TryParse(SocialHttp.StrOrNull(node, path), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    public static string Ext(string mime) => mime switch
    {
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "video/mp4" => "mp4",
        "video/quicktime" => "mov",
        _ => "jpg",
    };

    public static bool IsVideo(PublishMedia? media) => media?.Mime.StartsWith("video/", StringComparison.Ordinal) == true;

    public static SocialApiException Error(string message) => new(ErrorCodes.PubFailed, message, isTransient: false);

    /// <summary>ID がないときの説明（どの値が返らなかったか）。</summary>
    public static string Required(JsonNode? node, string path, string network) =>
        SocialHttp.StrOrNull(node, path) ?? throw Error($"{network}の応答に {path} がありません");

    /// <summary>各社のページが使う SNS の名前の一覧（広告の出し先）。</summary>
    public static IReadOnlyList<SocialPlatform> PlatformsOf(AdNetwork network) => network switch
    {
        AdNetwork.Meta => [SocialPlatform.Facebook, SocialPlatform.Instagram],
        AdNetwork.TikTok => [SocialPlatform.TikTok],
        AdNetwork.X => [SocialPlatform.X],
        AdNetwork.Google => [SocialPlatform.YouTube],
        _ => [],
    };

    /// <summary>失敗した出稿の後片付け（失敗しても元のエラーを優先する）。</summary>
    public static async Task CleanupAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex) when (ex is SocialApiException or HttpRequestException or TaskCanceledException)
        {
            // 後片付けに失敗しても、つくりかけは停止したままなので配信・請求はされない
        }
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
