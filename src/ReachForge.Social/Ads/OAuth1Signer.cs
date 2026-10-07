using System.Security.Cryptography;
using System.Text;

namespace ReachForge.Social.Ads;

/// <summary>OAuth 1.0a の署名（HMAC-SHA1。X Ads API）。署名にはクエリとフォームの値を含め、JSON・マルチパートの本文は含めない。</summary>
public static class OAuth1Signer
{
    public static string AuthorizationHeader(string method, Uri url, IEnumerable<KeyValuePair<string, string>>? formFields,
        string consumerKey, string consumerSecret, string? token, string? tokenSecret, IEnumerable<KeyValuePair<string, string>>? extraOAuth = null,
        string? nonce = null, long? timestamp = null)
    {
        var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["oauth_version"] = "1.0",
        };
        if (!string.IsNullOrEmpty(token)) oauth["oauth_token"] = token;
        foreach (var (k, v) in extraOAuth ?? []) oauth[k] = v;

        oauth["oauth_signature"] = Signature(method, url, oauth.Concat(Query(url)).Concat(formFields ?? []), consumerSecret, tokenSecret);
        return "OAuth " + string.Join(", ", oauth.Select(p => $"{Escape(p.Key)}=\"{Escape(p.Value)}\""));
    }

    public static string Signature(string method, Uri url, IEnumerable<KeyValuePair<string, string>> parameters, string consumerSecret, string? tokenSecret)
    {
        var normalized = string.Join('&', parameters
            .Select(p => (Key: Escape(p.Key), Value: Escape(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
        var baseUrl = $"{url.Scheme}://{url.Host}{(url.IsDefaultPort ? "" : ":" + url.Port)}{url.AbsolutePath}";
        var signatureBase = $"{method.ToUpperInvariant()}&{Escape(baseUrl)}&{Escape(normalized)}";
        var key = $"{Escape(consumerSecret)}&{Escape(tokenSecret ?? "")}";
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(signatureBase)));
    }

    /// <summary>RFC 3986 の percent-encoding（英数字と -._~ 以外）。</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var i = part.IndexOf('=');
            return i < 0
                ? new KeyValuePair<string, string>(Uri.UnescapeDataString(part), "")
                : new KeyValuePair<string, string>(Uri.UnescapeDataString(part[..i]), Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' ')));
        });
}
