using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ReachForge.Desktop.Host;

/// <summary>
/// PC の中だけで使う https://localhost 用の自己署名証明書。SNS の OAuth は HTTPS のコールバック URL を求めるため、
/// サーバーは HTTPS で待ち受け、画面（WebView2）はこの証明書だけを例外として信頼する（OS の信頼ストアには入れない）。
/// </summary>
public static class LocalCertificate
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(730);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>証明書を読み込む。なければ、または期限が近ければ作り直す。</summary>
    public static X509Certificate2 Ensure(string path, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (existing.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewBefore) return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // 壊れていれば作り直す
            }
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=ReachForge Desktop", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // サーバー認証
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Lifetime));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    /// <summary>証明書の SHA-256 の指紋（大文字の16進）。WebView2 とヘルスチェックで同じ証明書かを確かめる。</summary>
    public static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
}
