using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ReachForge.Desktop.Host;

/// <summary>起動できない理由（利用者に表示する）。</summary>
public sealed class DesktopStartupException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="ServerPath">サーバー（ReachForge.Web.exe、または開発時の ReachForge.Web.dll）。</param>
/// <param name="FfmpegPath">同梱した ffmpeg（なければ PATH の ffmpeg を使う）。</param>
public sealed record ServerLaunchOptions(string ServerPath, DesktopPaths Paths, int Port, string? FfmpegPath = null)
{
    /// <summary>サーバーに追加で渡す設定（例：お試し用のデモデータ <c>Database__SeedDemo=true</c>）。</summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// PC の中で動かすサーバー（ReachForge.Web）を子プロセスとして起動・停止する。
/// https://localhost:{Port} だけで待ち受け、起動ごとに作る秘密の値（LaunchToken）で自動ログインと終了を受け付ける。
/// 出力はデータフォルダーの logs に日付ごとに残す（14日分）。
/// </summary>
public sealed class ServerProcess : IAsyncDisposable
{
    public const int LogRetentionDays = 14;

    private readonly ServerLaunchOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient _http;
    private readonly object _logLock = new();
    private readonly Queue<string> _recent = new();
    private Process? _process;
    private StreamWriter? _log;
    private bool _stopping;

    public ServerProcess(ServerLaunchOptions options)
    {
        _options = options;
        options.Paths.EnsureCreated();
        _certificate = LocalCertificate.Ensure(options.Paths.Certificate);
        CertificateThumbprint = LocalCertificate.Thumbprint(_certificate);
        LaunchToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _http = new HttpClient(new HttpClientHandler
        {
            // 自分の証明書だけを信頼する
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || (cert is not null && IsOwnCertificate(cert)),
            AllowAutoRedirect = false,
        })
        { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(10) };
    }

    public Uri BaseUri => new($"https://localhost:{_options.Port}/");
    public string LaunchToken { get; }
    public string CertificateThumbprint { get; }

    /// <summary>WebView2 が最初に開く URL（自動ログイン）。</summary>
    public Uri SignInUri => new(BaseUri, $"desktop/signin?t={Uri.EscapeDataString(LaunchToken)}");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>サーバーが予期せず終了した（終了コード）。</summary>
    public event Action<int>? Exited;

    public bool IsOwnCertificate(X509Certificate2 certificate) =>
        string.Equals(LocalCertificate.Thumbprint(certificate), CertificateThumbprint, StringComparison.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        EnsurePortIsFree(_options.Port);
        if (!File.Exists(_options.ServerPath))
        {
            throw new DesktopStartupException($"サーバーが見つかりません（{_options.ServerPath}）。アプリを入れ直してください。");
        }
        OpenLog();

        var psi = _options.ServerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("dotnet") { ArgumentList = { _options.ServerPath } }
            : new ProcessStartInfo(_options.ServerPath);
        psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.ServerPath))!; // appsettings.json を読むため
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        // 利用者の環境の URL 指定は使わない（ポートは Kestrel の設定で決める）
        psi.Environment.Remove("ASPNETCORE_URLS");
        psi.Environment.Remove("ASPNETCORE_HTTP_PORTS");
        psi.Environment.Remove("ASPNETCORE_HTTPS_PORTS");
        foreach (var (key, value) in Environment(_options, LaunchToken)) psi.Environment[key] = value;

        _stopping = false;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Log(e.Data);
        _process.ErrorDataReceived += (_, e) => Log(e.Data);
        _process.Exited += (_, _) =>
        {
            var code = SafeExitCode();
            Log($"[desktop] server exited ({code})");
            if (!_stopping) Exited?.Invoke(code);
        };
        try
        {
            _process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DesktopStartupException($"サーバーを起動できませんでした（{ex.Message}）。", ex);
        }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitUntilReadyAsync(ct);
    }

    /// <summary>サーバーに渡す設定（環境変数）。DB・メディアはデータフォルダー、HTTPS は自己署名証明書。</summary>
    internal static IReadOnlyDictionary<string, string> Environment(ServerLaunchOptions o, string launchToken)
    {
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Desktop",
            ["Kestrel__Endpoints__Https__Url"] = $"https://localhost:{o.Port}",
            ["Kestrel__Endpoints__Https__Certificate__Path"] = o.Paths.Certificate,
            ["Desktop__Enabled"] = "true",
            ["Desktop__LaunchToken"] = launchToken,
            ["Desktop__ConfigPath"] = o.Paths.UserConfig,
            ["ConnectionStrings__ReachForge"] = $"Data Source={o.Paths.Database}",
            ["Media__LocalPath"] = o.Paths.Media,
            ["DOTNET_gcServer"] = "0", // PC で動かすためメモリを抑える
        };
        if (o.FfmpegPath is { } ffmpeg && File.Exists(ffmpeg)) env["Video__FfmpegPath"] = ffmpeg;
        foreach (var (key, value) in o.ExtraEnvironment) env.TryAdd(key, value); // 場所・秘密の値は上書きさせない
        return env;
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
            {
                throw new DesktopStartupException("サーバーが起動中に終了しました。" + RecentLog());
            }
            try
            {
                using var response = await _http.GetAsync("alive", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // まだ待ち受けていない
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                await StopAsync();
                throw new DesktopStartupException("サーバーの起動が時間内に終わりませんでした。" + RecentLog());
            }
            await Task.Delay(300, ct);
        }
    }

    /// <summary>止める。まずサーバーに終了を頼み（保存中の処理を終えるため）、時間内に終わらなければ強制終了する。</summary>
    public async Task StopAsync()
    {
        if (_process is null) return;
        _stopping = true;
        if (!_process.HasExited)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "desktop/shutdown");
                request.Headers.Add("X-Desktop-Token", LaunchToken);
                using var _ = await _http.SendAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 応答できない状態なら強制終了する
            }
            using var timeout = new CancellationTokenSource(_options.StopTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        _process.Dispose();
        _process = null;
        lock (_logLock)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    private int SafeExitCode()
    {
        try
        {
            return _process?.ExitCode ?? -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
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

    private void OpenLog()
    {
        var dir = _options.Paths.Logs;
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "server-*.log")
                     .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-LogRetentionDays)))
        {
            File.Delete(old);
        }
        lock (_logLock)
        {
            _log?.Dispose();
            var file = new FileStream(Path.Combine(dir, $"server-{DateTime.Now:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _log = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    private void Log(string? line)
    {
        if (line is null) return;
        lock (_logLock)
        {
            _log?.WriteLine(line);
            _recent.Enqueue(line);
            while (_recent.Count > 20) _recent.Dequeue();
        }
    }

    private string RecentLog()
    {
        lock (_logLock)
        {
            return _recent.Count == 0 ? "" : "\n\n" + string.Join('\n', _recent.TakeLast(8));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _certificate.Dispose();
    }
}
