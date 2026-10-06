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
    private readonly Dictionary<string, string?> _settings;

    public WebFixture(Dictionary<string, string?>? overrides = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReachForge"] = $"Data Source={_db}",
            ["Database:SeedDemo"] = "true",
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
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
    }
}

public static class HttpAssert
{
    public static string Location(HttpResponseMessage r) =>
        r.Headers.Location?.ToString() ?? throw new Xunit.Sdk.XunitException($"No redirect (status {(int)r.StatusCode})");

    public static bool IsRedirect(HttpResponseMessage r) => r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found;
}
