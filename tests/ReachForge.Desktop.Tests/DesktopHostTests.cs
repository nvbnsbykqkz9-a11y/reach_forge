using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using ReachForge.Desktop.Host;

namespace ReachForge.Desktop.Tests;

public sealed class DesktopHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-desktop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Paths_are_created_with_a_config_template()
    {
        var paths = new DesktopPaths(_root).EnsureCreated();
        Assert.True(Directory.Exists(paths.Media));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.Database)));
        Assert.Contains("\"anthropic\"", File.ReadAllText(paths.UserConfig));

        File.WriteAllText(paths.UserConfig, "{ \"mine\": true }");
        paths.EnsureCreated();
        Assert.Contains("mine", File.ReadAllText(paths.UserConfig)); // 利用者の編集は上書きしない
    }

    [Fact]
    public void Settings_round_trip_and_tolerate_broken_files()
    {
        var path = Path.Combine(Directory.CreateDirectory(_root).FullName, "desktop.json");
        Assert.Equal(47120, DesktopSettings.Load(path).Port);
        new DesktopSettings { Port = 50123, TrayNoticeShown = true }.Save(path);
        Assert.Equal(new DesktopSettings { Port = 50123, TrayNoticeShown = true }, DesktopSettings.Load(path));
        File.WriteAllText(path, "{ broken");
        Assert.Equal(new DesktopSettings(), DesktopSettings.Load(path));
    }

    [Fact]
    public void Certificate_is_created_once_for_localhost_and_renewed_near_expiry()
    {
        var path = Path.Combine(_root, "certs", "localhost.pfx");
        using var first = LocalCertificate.Ensure(path);
        using var again = LocalCertificate.Ensure(path);
        Assert.Equal(LocalCertificate.Thumbprint(first), LocalCertificate.Thumbprint(again));
        Assert.True(first.HasPrivateKey);
        var san = first.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());

        var nearExpiry = new FixedClock(DateTimeOffset.UtcNow + LocalCertificate.Lifetime - TimeSpan.FromDays(1));
        using var renewed = LocalCertificate.Ensure(path, nearExpiry);
        Assert.NotEqual(LocalCertificate.Thumbprint(first), LocalCertificate.Thumbprint(renewed));
    }

    [Fact]
    public void Settings_point_data_to_the_data_folder_and_cannot_be_overridden()
    {
        var paths = new DesktopPaths(_root);
        var settings = DesktopServer.Settings(new DesktopServerOptions(paths, 50000)
        {
            ExtraSettings = new Dictionary<string, string?> { ["Desktop:LaunchToken"] = "other", ["Database:SeedDemo"] = "true" },
        }, "tok");
        Assert.Equal($"Data Source={paths.Database}", settings["ConnectionStrings:ReachForge"]);
        Assert.Equal(paths.Media, settings["Media:LocalPath"]);
        Assert.Equal("tok", settings["Desktop:LaunchToken"]);
        Assert.Equal("true", settings["Database:SeedDemo"]);
        Assert.False(settings.ContainsKey("Video:FfmpegPath")); // 同梱していなければ PATH の ffmpeg
    }

    [Fact]
    public void Busy_port_is_reported_instead_of_moving()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ex = Assert.Throws<DesktopStartupException>(() => DesktopServer.EnsurePortIsFree(port));
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task App_runs_in_process_on_https_signs_in_locally_and_stops()
    {
        var paths = new DesktopPaths(_root);
        await using var server = new DesktopServer(new DesktopServerOptions(paths, FreePort()));
        await server.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(server.IsRunning);

        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && server.IsOwnCertificate(cert),
        }) { BaseAddress = server.BaseUri };

        // 利用者がまだいなければ初回登録へ
        using (var signIn = await http.GetAsync(server.SignInUri, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal("/account/register", signIn.Headers.Location?.OriginalString);
        }
        // 秘密の値が違えば何も起きない
        using (var wrong = await http.GetAsync("desktop/signin?t=wrong", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        // DB はマイグレーションで作られる（版を上げても利用者のデータを残すため）
        await using (var db = new SqliteConnection($"Data Source={paths.Database};Mode=ReadOnly"))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
            Assert.True((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= 1);
        }

        await server.StopAsync();
        Assert.False(server.IsRunning);
        Assert.Contains(Directory.GetFiles(paths.Logs), f => Path.GetFileName(f).StartsWith(FileLoggerProvider.FilePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_owner_is_signed_in_automatically()
    {
        var paths = new DesktopPaths(_root);
        await using var server = new DesktopServer(new DesktopServerOptions(paths, FreePort())
        {
            ExtraSettings = new Dictionary<string, string?> { ["Database:SeedDemo"] = "true" },
        });
        await server.StartAsync(TestContext.Current.CancellationToken);

        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = cookies,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && server.IsOwnCertificate(cert),
        }) { BaseAddress = server.BaseUri };
        using var home = await http.GetAsync(server.SignInUri, TestContext.Current.CancellationToken); // → / へ転送
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("/", home.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains(cookies.GetCookies(server.BaseUri).Cast<Cookie>(), c => c.Name == "rf.auth" && c.Secure);
        // ビルド結果から起動しても（F5）、画面の CSS・スクリプトが配信される
        foreach (var asset in new[] { "_content/MudBlazor/MudBlazor.min.css", "_framework/blazor.web.js" })
        {
            using var response = await http.GetAsync(asset, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{asset}: {(int)response.StatusCode}");
        }

        await server.DisposeAsync();
        var log = string.Join('\n', Directory.GetFiles(paths.Logs).Select(f => File.ReadAllText(f)));
        Assert.DoesNotContain("fail:", log);
        Assert.DoesNotContain("crit:", log);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
