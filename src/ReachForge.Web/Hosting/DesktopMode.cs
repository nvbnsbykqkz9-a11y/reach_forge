using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ReachForge.Infrastructure.Identity;

namespace ReachForge.Web.Hosting;

/// <summary>
/// Windows 版（PC 単体）の設定。デスクトップアプリ（ReachForge.Desktop）がこのサーバーを子プロセスとして
/// https://localhost の固定ポートで起動し、WebView2 で画面を表示する。
/// </summary>
public sealed class DesktopOptions
{
    public const string SectionName = "Desktop";

    public bool Enabled { get; set; }

    /// <summary>起動ごとにデスクトップアプリが作る秘密の値。自動ログインと終了の要求に使う（環境変数で渡し、保存しない）。</summary>
    public string? LaunchToken { get; set; }

    /// <summary>利用者が編集する設定ファイル（API キーなど）。データフォルダーに置き、変更はすぐ反映する。</summary>
    public string? ConfigPath { get; set; }
}

public static class DesktopMode
{
    public const string SignInPath = "/desktop/signin";
    public const string ShutdownPath = "/desktop/shutdown";
    public const string TokenHeader = "X-Desktop-Token";

    /// <summary>Windows 版の設定を読み込む。利用者の設定ファイルを追加し、Windows では暗号鍵を DPAPI（利用者単位）で保護する。</summary>
    public static WebApplicationBuilder AddDesktopMode(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(DesktopOptions.SectionName).Get<DesktopOptions>() ?? new DesktopOptions();
        builder.Services.Configure<DesktopOptions>(builder.Configuration.GetSection(DesktopOptions.SectionName));
        if (!options.Enabled) return builder;

        if (!string.IsNullOrEmpty(options.ConfigPath))
        {
            builder.Configuration.AddJsonFile(options.ConfigPath, optional: true, reloadOnChange: true);
            // デスクトップアプリが環境変数で渡す値（DB の場所・ポートなど）を、利用者の設定ファイルより優先する
            builder.Configuration.AddEnvironmentVariables();
        }
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddDataProtection().ProtectKeysWithDpapi();
        }
        // 開発中（F5・ビルド結果から起動）は MudBlazor などの静的ファイルが wwwroot にないため、開発用の一覧から配信する。
        // 発行した形では一覧のファイルがないので何もしない
        builder.WebHost.UseStaticWebAssets();
        return builder;
    }

    public static void MapDesktopEndpoints(this WebApplication app)
    {
        var options = app.Configuration.GetSection(DesktopOptions.SectionName).Get<DesktopOptions>() ?? new DesktopOptions();
        if (!options.Enabled || string.IsNullOrEmpty(options.LaunchToken)) return;
        var token = options.LaunchToken;

        // デスクトップアプリからの自動ログイン。PC の利用者は1人なので、最初に登録した利用者（オーナー）でログインする。
        // まだ誰もいなければ初回登録の画面へ進む
        app.MapGet(SignInPath, async (HttpContext http, string? t, SignInManager<AppUser> signIn, UserManager<AppUser> users,
            AccountService accounts, CancellationToken ct) =>
        {
            if (!IsLocal(http) || !Matches(t, token)) return Results.NotFound();
            var user = await users.Users.OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(ct);
            if (user is null) return Results.LocalRedirect("/account/register");
            if (await users.IsLockedOutAsync(user)) return Results.LocalRedirect("/account/login");
            await signIn.SignInAsync(user, isPersistent: true);
            await accounts.AuditAsync(user, "auth.login", "desktop", ct);
            return Results.LocalRedirect("/");
        }).AllowAnonymous().ExcludeFromDescription();

        // デスクトップアプリの終了時に、処理中の保存を終えてから止める
        app.MapPost(ShutdownPath, (HttpContext http, IHostApplicationLifetime lifetime) =>
        {
            if (!IsLocal(http) || !Matches(http.Request.Headers[TokenHeader], token)) return Results.NotFound();
            lifetime.StopApplication();
            return Results.Accepted();
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
    }

    private static bool IsLocal(HttpContext http) => http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    private static bool Matches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
