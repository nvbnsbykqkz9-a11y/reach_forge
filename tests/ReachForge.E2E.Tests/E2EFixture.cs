using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Ai;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>
/// ブラウザでの E2E テスト用に、Web アプリを実際のポート（Kestrel）で起動し、Playwright の Chromium を用意する。
/// 開発環境の設定（デモデータ・スタブ AI・モック SNS・Web 内でのジョブ実行）で動かし、DB とメディアは一時フォルダに置く。
/// ブラウザは PLAYWRIGHT_CHROMIUM（実行ファイル）→ /opt/pw-browsers の Chromium → Playwright の既定（playwright install 済み）の順に探す。
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const string Password = "ReachForge#2026";

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rf-e2e-{Guid.NewGuid():N}.db");
    private readonly string _media = Path.Combine(Path.GetTempPath(), $"rf-e2e-media-{Guid.NewGuid():N}");
    private App? _app;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = default!;
    public string BaseUrl { get; private set; } = "";

    private sealed class App(string db, string media, int port) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); // 静的ファイル（MudBlazor など）を開発時と同じく配信する
            builder.UseSetting("ConnectionStrings:ReachForge", $"Data Source={db}");
            builder.UseSetting("Media:LocalPath", media);
            builder.UseSetting("Database:SeedDemo", "true");
            builder.UseSetting("Worker:RunInWeb", "true");
            builder.UseSetting("Auth:RequireMfaForAdmins", "false");
            builder.UseSetting("Ops:Operators:0", "owner@example.com");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            // 外部のサイトには出ない：LP の取得は決まったページを返す
            builder.ConfigureTestServices(s => s.AddSingleton<IWebPageFetcher, FakeLandingPageFetcher>());
            UseKestrel(port);
        }
    }

    public async ValueTask InitializeAsync()
    {
        var port = FreePort();
        _app = new App(_db, _media, port);
        _app.StartServer();
        BaseUrl = $"http://127.0.0.1:{port}";

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = FindChromium() });
    }

    public async Task<IPage> NewPageAsync(int width = 1440, int height = 1000)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }, Locale = "ja-JP", BaseURL = BaseUrl,
        });
        return await context.NewPageAsync();
    }

    public async Task<IPage> LoginAsync(string email = "owner@example.com", int width = 1440, int height = 1000)
    {
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/account/login");
        await page.GetByLabel("メールアドレス").FillAsync(email);
        await page.GetByLabel("パスワード").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "ログイン", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/account/login", StringComparison.Ordinal));
        return page;
    }

    /// <summary>Blazor のサーキットがつながるまで待つ（事前描画のままだとクリックが効かない）。</summary>
    public static async Task WaitForInteractiveAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(500);
    }

    private static string? FindChromium()
    {
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM") is { Length: > 0 } configured) return configured;
        const string root = "/opt/pw-browsers";
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "chromium-*").OrderDescending()
            .Select(d => Path.Combine(d, "chrome-linux", "chrome"))
            .FirstOrDefault(File.Exists);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
        if (Directory.Exists(_media)) Directory.Delete(_media, recursive: true);
    }
}

/// <summary>E2E 用の LP（ページと画像）。画像はテスト内で描いたものを返す。</summary>
public sealed class FakeLandingPageFetcher : IWebPageFetcher
{
    public const string Url = "https://lp.example.com/autumn-latte";

    public Task<WebPage> FetchAsync(string url, CancellationToken ct) => Task.FromResult(new WebPage(new Uri(Url),
        "秋限定さつまいもラテ | ほっこりカフェ", "10月末までの期間限定メニュー",
        "秋限定さつまいもラテ 680円。焼きいもの香ばしさとミルクのやさしい甘さ。", ["#B45309"],
        [
            new WebImage(new Uri("https://lp.example.com/og.jpg"), null, IsShareImage: true),
            new WebImage(new Uri("https://lp.example.com/latte.jpg"), "さつまいもラテ"),
        ]));

    public async Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct)
    {
        var image = await new ReachForge.Infrastructure.Media.ImageSharpProcessor()
            .RenderPlaceholderAsync(1200, 900, url.AbsolutePath.Length, ["#B45309", "#FDE68A"], ct);
        return new FetchedImage(image.Bytes, image.Mime);
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "e2e";
}
