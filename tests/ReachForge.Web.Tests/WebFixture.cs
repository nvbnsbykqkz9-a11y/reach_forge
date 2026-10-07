using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ReachForge.Web.Tests;

/// <summary>テストごとの SQLite・スタブAI・モックSNS・デモデータで Web アプリを起動する。</summary>
public sealed class WebFixture : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rf-web-{Guid.NewGuid():N}.db");
    private readonly string _media = Path.Combine(Path.GetTempPath(), $"rf-web-media-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _settings;

    private readonly string? _pgDatabase;

    public WebFixture(Dictionary<string, string?>? overrides = null)
    {
        // RF_TEST_POSTGRES（管理者の接続文字列）があれば PostgreSQL（RLS 付き・アプリ専用ロール）で実行する
        string? pg = null;
        if (Environment.GetEnvironmentVariable("RF_TEST_POSTGRES") is { } admin)
        {
            _pgDatabase = $"rf_w_{Guid.NewGuid():N}";
            pg = PostgresTestDatabase.Create(admin, _pgDatabase);
        }
        _settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = pg is null ? "Sqlite" : "Postgres",
            ["ConnectionStrings:ReachForge"] = pg ?? $"Data Source={_db}",
            ["Database:SeedDemo"] = "true",
            ["Media:LocalPath"] = _media,
            ["Worker:RunInWeb"] = "false",
            ["Social:UseMock"] = "true",
            ["Social:Meta:AppSecret"] = "meta-secret",
            ["Social:Meta:WebhookVerifyToken"] = "verify-me",
            ["Auth:RequireMfaForAdmins"] = "false",
            ["AI:Providers:local:Type"] = "Stub",
            ["AI:Routes:Default:0"] = "local",
        };
        foreach (var (k, v) in overrides ?? []) _settings[k] = v;
    }

    /// <summary>送信されたメール（テスト用に送信せず記録する）。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<ReachForge.Application.Abstractions.EmailMessage> Emails { get; } = new();

    /// <summary>サーバー側のエラーログ（テストの失敗原因の表示用）。</summary>
    public ReachForge.Application.Tests.CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // 開発環境と同じく、スコープ付きサービスをルートから解決していないかを検査する
        builder.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureServices(s => s.AddSingleton<ReachForge.Application.Abstractions.IEmailSender>(new CapturingEmailSender(Emails)));
        builder.ConfigureLogging(l => l.AddProvider(Logs));
    }

    private sealed class CapturingEmailSender(System.Collections.Concurrent.ConcurrentQueue<ReachForge.Application.Abstractions.EmailMessage> sink)
        : ReachForge.Application.Abstractions.IEmailSender
    {
        public Task SendAsync(ReachForge.Application.Abstractions.EmailMessage message, CancellationToken ct)
        {
            sink.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    /// <summary>ログイン画面のフォームを取得して POST する（偽造防止トークン付き）。</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password = "ReachForge#2026")
    {
        var html = await client.GetStringAsync("/account/login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "login",
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
        if (Directory.Exists(_media)) Directory.Delete(_media, recursive: true);
        if (_pgDatabase is not null) PostgresTestDatabase.Drop(Environment.GetEnvironmentVariable("RF_TEST_POSTGRES")!, _pgDatabase);
    }
}

public static class HttpAssert
{
    public static string Location(HttpResponseMessage r) =>
        r.Headers.Location?.ToString() ?? throw new Xunit.Sdk.XunitException($"No redirect (status {(int)r.StatusCode})");

    public static bool IsRedirect(HttpResponseMessage r) => r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found;
}
