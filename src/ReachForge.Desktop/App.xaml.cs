using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ReachForge.Desktop.Host;

namespace ReachForge.Desktop;

/// <summary>
/// Windows 版の本体。サーバー（server\ReachForge.Web.exe）を起動して WebView2 で表示し、
/// ウィンドウを閉じてもタスクトレイに残って予約投稿・指標の取得・受信箱の取り込みを続ける。終了はトレイのメニューから。
/// </summary>
public partial class App : Application
{
    public const string MinimizedArgument = "--minimized";

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public DesktopPaths Paths { get; } = DesktopPaths.Default;
    public DesktopSettings Settings { get; private set; } = new();
    public ServerProcess? Server { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance("ReachForge.Desktop");
        if (!_instance.IsFirst)
        {
            // すでに起動していれば、そのウィンドウを前に出して終わる
            _instance.ActivateFirst();
            Shutdown();
            return;
        }
        _instance.Activated += () => Dispatcher.Invoke(ShowWindow);

        Paths.EnsureCreated();
        Settings = DesktopSettings.Load(Paths.Settings);
        _tray = new TrayIcon(this);
        _window = new MainWindow(this);
        if (!e.Args.Contains(MinimizedArgument)) _window.Show();
        _ = StartServerAsync();
    }

    /// <summary>サーバーを起動して画面を開く。失敗したら理由とやり直しのボタンを表示する。</summary>
    public async Task StartServerAsync()
    {
        _window?.ShowStatus("起動しています…");
        try
        {
            if (Server is not null) await Server.DisposeAsync();
            var baseDir = AppContext.BaseDirectory;
            Server = new ServerProcess(new ServerLaunchOptions(
                ServerLocator.Find(baseDir, BuildConfiguration), Paths, Settings.Port,
                Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe")));
            Server.Exited += code => Dispatcher.Invoke(() => OnServerExited(code));
            await Server.StartAsync();
            if (_window is not null) await _window.OpenAsync(Server);
        }
        catch (DesktopStartupException ex)
        {
            _window?.ShowError(ex.Message);
            ShowWindow();
        }
        catch (Exception ex)
        {
            _window?.ShowError($"起動できませんでした（{ex.Message}）。");
            ShowWindow();
        }
    }

    private void OnServerExited(int code)
    {
        if (_exiting) return;
        _window?.ShowError($"サーバーが停止しました（終了コード {code}）。予約投稿は止まっています。");
        _tray?.Notify("ReachForge が停止しました", "画面を開いて「もう一度起動する」を押してください。");
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>ウィンドウを閉じたとき。トレイに残す設定なら隠し、初回だけ知らせる。</summary>
    public bool KeepRunningOnClose()
    {
        if (_exiting || !Settings.KeepRunningInTray) return false;
        if (!Settings.TrayNoticeShown)
        {
            _tray?.Notify("ReachForge は動作を続けています", "予約投稿を続けるため、タスクトレイに残っています。終了はトレイのアイコンから選べます。");
            Settings = Settings with { TrayNoticeShown = true };
            Settings.Save(Paths.Settings);
        }
        return true;
    }

    /// <summary>終了する。サーバーに保存中の処理を終えてもらってから止める。</summary>
    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.ShowStatus("終了しています…");
        if (Server is not null) await Server.DisposeAsync();
        _tray?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }

    public static void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void OpenExternal(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows のサインアウト・シャットダウン：サーバーを止めてから終わる
        _exiting = true;
        Server?.StopAsync().Wait(TimeSpan.FromSeconds(10));
        base.OnSessionEnding(e);
    }
}
