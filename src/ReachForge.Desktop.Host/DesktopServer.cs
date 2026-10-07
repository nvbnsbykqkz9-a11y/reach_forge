using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Web.Hosting;

namespace ReachForge.Desktop.Host;

/// <summary>起動できない理由（利用者に表示する）。</summary>
public sealed class DesktopStartupException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="FfmpegPath">同梱した ffmpeg（なければ PATH の ffmpeg を使う）。</param>
public sealed record DesktopServerOptions(DesktopPaths Paths, int Port, string? FfmpegPath = null)
{
    /// <summary>追加の設定（例：お試し用のデモデータ <c>Database:SeedDemo=true</c>）。データの場所・自動ログインの値は上書きできない。</summary>
    public IReadOnlyDictionary<string, string?> ExtraSettings { get; init; } = new Dictionary<string, string?>();

    /// <summary>アプリのファイル（appsettings.json・wwwroot）の場所。既定は実行ファイルのフォルダー。</summary>
    public string ContentRoot { get; init; } = AppContext.BaseDirectory;

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Windows 版の本体：Web 版と同じアプリ（ReachForge.Web）を、デスクトップアプリと同じプロセスの中で起動する。
/// https://localhost:{Port} だけで待ち受け（外部からは接続できない）、起動ごとに作る秘密の値（LaunchToken）で自動ログインする。
/// 予約投稿・指標の取得・受信箱の取り込みなどの定期処理も同じプロセスで動く。ログはデータフォルダーの logs に残す。
/// </summary>
public sealed class DesktopServer : IAsyncDisposable
{
    private readonly DesktopServerOptions _options;
    private readonly X509Certificate2 _certificate;
    private WebApplication? _app;
    private FileLoggerProvider? _log;
    private bool _stopping;

    public DesktopServer(DesktopServerOptions options)
    {
        _options = options;
        options.Paths.EnsureCreated();
        _certificate = LocalCertificate.Ensure(options.Paths.Certificate);
        CertificateThumbprint = LocalCertificate.Thumbprint(_certificate);
        LaunchToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public Uri BaseUri => new($"https://localhost:{_options.Port}/");
    public string LaunchToken { get; }
    public string CertificateThumbprint { get; }

    /// <summary>WebView2 が最初に開く URL（自動ログイン）。</summary>
    public Uri SignInUri => new(BaseUri, $"desktop/signin?t={Uri.EscapeDataString(LaunchToken)}");

    public bool IsRunning { get; private set; }

    /// <summary>今日のログファイル（起動に失敗したときの案内に使う）。</summary>
    public string? LogFile => _log?.CurrentFile;

    /// <summary>止めるよう頼んでいないのに止まった（致命的なエラー）。</summary>
    public event Action? StoppedUnexpectedly;

    public bool IsOwnCertificate(X509Certificate2 certificate) =>
        string.Equals(LocalCertificate.Thumbprint(certificate), CertificateThumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>アプリに渡す設定。DB・メディアはデータフォルダー、Windows 版の動き（定期処理を同じプロセスで等）は appsettings.Desktop.json。</summary>
    internal static Dictionary<string, string?> Settings(DesktopServerOptions o, string launchToken)
    {
        var settings = new Dictionary<string, string?>(o.ExtraSettings);
        settings["Desktop:Enabled"] = "true";
        settings["Desktop:LaunchToken"] = launchToken;
        settings["Desktop:ConfigPath"] = o.Paths.UserConfig;
        settings["ConnectionStrings:ReachForge"] = $"Data Source={o.Paths.Database}";
        settings["Media:LocalPath"] = o.Paths.Media;
        if (o.FfmpegPath is { } ffmpeg && File.Exists(ffmpeg)) settings["Video:FfmpegPath"] = ffmpeg;
        return settings;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        EnsurePortIsFree(_options.Port);
        _log ??= new FileLoggerProvider(_options.Paths.Logs);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "ReachForge.Web", // 画面の CSS・スクリプトの一覧（ReachForge.Web.staticwebassets.*.json）を見つけるため
            ContentRootPath = _options.ContentRoot,
            EnvironmentName = "Desktop",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_log);
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(_options.Port, l => l.UseHttps(_certificate)));
        _stopping = false;
        try
        {
            _app = await ReachForgeApp.CreateAsync(builder, Settings(_options, LaunchToken));
            _app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(() =>
            {
                IsRunning = false;
                if (!_stopping) StoppedUnexpectedly?.Invoke();
            });
            await _app.StartAsync(ct);
            IsRunning = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DesktopStartupException)
        {
            _log.CreateLogger("ReachForge.Desktop").LogCritical(ex, "Failed to start");
            await DisposeAppAsync();
            throw new DesktopStartupException($"起動できませんでした（{ex.Message}）。", ex);
        }
    }

    /// <summary>止める。処理中の要求・保存を終えるまで待つ（上限 <see cref="DesktopServerOptions.StopTimeout"/>）。</summary>
    public async Task StopAsync()
    {
        if (_app is null) return;
        _stopping = true;
        using var timeout = new CancellationTokenSource(_options.StopTimeout);
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 時間内に終わらなければそのまま閉じる
        }
        await DisposeAppAsync();
    }

    private async Task DisposeAppAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _app = null;
        IsRunning = false;
    }

    /// <summary>
    /// ポートが空いているか確かめる。SNS アプリに登録したコールバック URL が変わらないよう、空いていなくても別のポートには逃げない。
    /// </summary>
    internal static void EnsurePortIsFree(int port)
    {
        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            throw new DesktopStartupException(
                $"ポート {port} を別のアプリが使っています。ReachForge がすでに起動していないか確認するか、設定（desktop.json の Port）を変えてください。" +
                "ポートを変えた場合は、SNS アプリに登録したコールバック URL も変える必要があります。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _log?.Dispose();
        _log = null;
        _certificate.Dispose();
    }
}
