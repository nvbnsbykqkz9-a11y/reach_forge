using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Common;

namespace ReachForge.Infrastructure.Web;

/// <summary>
/// ブランド診断用の Web ページ取得（F-02 処理 1）。サーバー側から任意の URL を取得するため SSRF を防ぐ：
/// ・http/https、既定ポート（80/443）のみ
/// ・接続先の IP をすべて検査し、ループバック・プライベート・リンクローカル・メタデータ等は拒否（接続時にも再検査）
/// ・リダイレクトは自前で追い、各ホップを同じ検査にかける（最大3回）
/// ・robots.txt の User-agent: * の Disallow を尊重
/// ・本文 2MB・10 秒まで
/// </summary>
public sealed partial class SafeWebPageFetcher(IHttpClientFactory http) : IWebPageFetcher
{
    public const string HttpClientName = "brand-fetch";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRedirects = 3;
    public const string UserAgent = "ReachForgeBot/1.0 (+brand diagnosis)";

    public async Task<WebPage> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) throw Unavailable("URLの形式が正しくありません。");
        var client = http.CreateClient(HttpClientName);
        for (var hop = 0; ; hop++)
        {
            EnsureAllowedUri(uri);
            await EnsurePublicHostAsync(uri.Host, ct);
            if (!await RobotsAllowAsync(client, uri, ct)) throw Unavailable("このサイトは自動取得を許可していません（robots.txt）。");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw Unavailable("サイトに接続できませんでした。");
            }
            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) throw Unavailable("転送が多すぎます。");
                    uri = new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw Unavailable($"ページを取得できませんでした（HTTP {(int)response.StatusCode}）。");
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) throw Unavailable("HTMLのページを指定してください。");
                var html = await ReadLimitedAsync(response, cts.Token);
                return Parse(uri, html);
            }
        }
    }

    internal static void EnsureAllowedUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) throw Unavailable("http または https のURLを指定してください。");
        if (!uri.IsDefaultPort) throw Unavailable("標準のポート（80/443）のURLを指定してください。");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw Unavailable("ユーザー名を含むURLは使えません。");
    }

    private static async Task EnsurePublicHostAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            throw Unavailable("サイトが見つかりませんでした。");
        }
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw Unavailable("このURLは取得できません（社内・非公開のアドレス）。");
    }

    /// <summary>グローバルに到達可能なアドレスか（プライベート・ループバック・リンクローカル・CGNAT・マルチキャスト等を除く）。</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)            // リンクローカル（クラウドのメタデータ 169.254.169.254 を含む）
                     || (b[0] == 100 && b[1] is >= 64 and <= 127) // CGNAT
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 198 && b[1] is 18 or 19));
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }

    private static async Task<bool> RobotsAllowAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/robots.txt"));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return true; // robots.txt がなければ許可
            return RobotsAllows(await response.Content.ReadAsStringAsync(cts.Token), uri.AbsolutePath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return true;
        }
    }

    /// <summary>User-agent: * のグループの Disallow（前方一致）。Allow の方が長く一致すれば許可。</summary>
    internal static bool RobotsAllows(string robots, string path)
    {
        var applies = false;
        string? disallow = null, allow = null;
        foreach (var raw in robots.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var (key, value) = (parts[0].Trim().ToLowerInvariant(), parts[1].Trim());
            if (key == "user-agent") applies = value == "*" || value.StartsWith("ReachForgeBot", StringComparison.OrdinalIgnoreCase);
            else if (applies && key == "disallow" && value.Length > 0 && path.StartsWith(value, StringComparison.Ordinal))
            {
                if (disallow is null || value.Length > disallow.Length) disallow = value;
            }
            else if (applies && key == "allow" && value.Length > 0 && path.StartsWith(value, StringComparison.Ordinal))
            {
                if (allow is null || value.Length > allow.Length) allow = value;
            }
        }
        return disallow is null || (allow is not null && allow.Length >= disallow.Length);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var total = 0;
        int read;
        while (total < MaxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0) total += read;
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(buffer, 0, total);
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|template)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blocks();
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();
    [GeneratedRegex(@"<meta\s+[^>]*(?:name|property)\s*=\s*[""'](?:description|og:description)[""'][^>]*content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescription();
    [GeneratedRegex(@"<meta\s+[^>]*name\s*=\s*[""']theme-color[""'][^>]*content\s*=\s*[""'](#[0-9a-fA-F]{6})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeColor();
    [GeneratedRegex(@"#[0-9a-fA-F]{6}\b")]
    private static partial Regex HexColor();
    [GeneratedRegex(@"<(br|p|div|li|h[1-6]|tr|section|article)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"[ \t　]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    internal static WebPage Parse(Uri url, string html)
    {
        var title = WebUtility.HtmlDecode(TitleTag().Match(html).Groups[1].Value).Trim();
        var description = WebUtility.HtmlDecode(MetaDescription().Match(html).Groups[1].Value).Trim();
        // 色：theme-color を優先し、ページ内で多く使われている色（白・黒・灰色を除く）を加える
        var colors = new List<string>();
        if (ThemeColor().Match(html) is { Success: true } theme) colors.Add(theme.Groups[1].Value.ToUpperInvariant());
        colors.AddRange(HexColor().Matches(html).Select(m => m.Value.ToUpperInvariant())
            .Where(c => !IsGray(c)).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5));
        var body = Blocks().Replace(html, " ");
        body = BlockTags().Replace(body, "\n");
        body = WebUtility.HtmlDecode(Tags().Replace(body, " "));
        body = BlankLines().Replace(Spaces().Replace(body, " "), "\n").Trim();
        if (body.Length > 12000) body = body[..12000];
        return new WebPage(url, title, description, body, colors.Distinct().Take(4).ToList());
    }

    private static bool IsGray(string hex)
    {
        var r = Convert.ToInt32(hex[1..3], 16);
        var g = Convert.ToInt32(hex[3..5], 16);
        var b = Convert.ToInt32(hex[5..7], 16);
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 24;
    }

    private static DomainException Unavailable(string message) =>
        new(ErrorCodes.BrdUrlUnavailable, $"{message}お手数ですが、内容を手入力するか、紹介文を貼り付けてください。");
}
