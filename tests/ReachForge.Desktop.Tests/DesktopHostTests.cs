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
        Assert.Contains("\"TikTok\"", File.ReadAllText(paths.UserConfig));

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
    public void Server_environment_points_data_to_the_data_folder()
    {
        var paths = new DesktopPaths(_root);
        var env = ServerProcess.Environment(new ServerLaunchOptions("server/ReachForge.Web.exe", paths, 50000), "tok");
        Assert.Equal("Desktop", env["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("https://localhost:50000", env["Kestrel__Endpoints__Https__Url"]);
        Assert.Equal($"Data Source={paths.Database}", env["ConnectionStrings__ReachForge"]);
        Assert.Equal(paths.Media, env["Media__LocalPath"]);
        Assert.Equal("tok", env["Desktop__LaunchToken"]);
        Assert.False(env.ContainsKey("Video__FfmpegPath")); // 同梱していなければ PATH の ffmpeg
    }

    [Fact]
    public void Busy_port_is_reported_instead_of_moving()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ex = Assert.Throws<DesktopStartupException>(() => ServerProcess.EnsurePortIsFree(port));
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Server_starts_on_https_signs_in_locally_and_stops_gracefully()
    {
        var paths = new DesktopPaths(_root);
        await using var server = new ServerProcess(new ServerLaunchOptions(WebServerPath(), paths, FreePort()));
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
        using (var shutdown = await http.PostAsync("desktop/shutdown", null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, shutdown.StatusCode); // ヘッダーなしの終了要求は受け付けない
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
        Assert.Contains(Directory.GetFiles(paths.Logs), f => Path.GetFileName(f).StartsWith("server-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_owner_is_signed_in_automatically()
    {
        var paths = new DesktopPaths(_root);
        await using var server = new ServerProcess(new ServerLaunchOptions(WebServerPath(), paths, FreePort())
        {
            ExtraEnvironment = new Dictionary<string, string> { ["Database__SeedDemo"] = "true", ["Desktop__LaunchToken"] = "overridden" },
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

        await server.StopAsync();
        var log = string.Join('\n', Directory.GetFiles(paths.Logs).Select(File.ReadAllText));
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

    /// <summary>ビルド済みのサーバー（src/ReachForge.Web/bin/{構成}/net10.0/ReachForge.Web.dll）。</summary>
    private static string WebServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReachForge.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name; // bin/{構成}/net10.0
        return Path.Combine(dir.FullName, "src", "ReachForge.Web", "bin", configuration, "net10.0", "ReachForge.Web.dll");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class ServerLocatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("rf-locator-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root.FullName, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Prefers_environment_then_bundled_then_repository_build()
    {
        var app = Path.Combine(_root.FullName, "src", "ReachForge.Desktop", "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(app);
        Touch("ReachForge.sln");
        var built = Touch("src", "ReachForge.Web", "bin", "Debug", "net10.0", "ReachForge.Web.exe");

        Assert.Equal(built, ServerLocator.Find(app, "Debug", _ => null)); // F5：リポジトリのビルド結果
        var bundled = Touch("src", "ReachForge.Desktop", "bin", "Debug", "net10.0-windows", "server", "ReachForge.Web.exe");
        Assert.Equal(bundled, ServerLocator.Find(app, "Debug", _ => null)); // 発行した形
        Assert.Equal("C:/custom/ReachForge.Web.exe", ServerLocator.Find(app, "Debug", _ => "C:/custom/ReachForge.Web.exe"));
    }
}
