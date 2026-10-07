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
/// Windows 版の本体。Web 版と同じアプリをこのプロセスの中で起動して WebView2 で表示し、
/// ウィンドウを閉じてもタスクトレイに残って予約投稿・指標の取得・受信箱の取り込みを続ける。終了はトレイのメニューから。
/// </summary>
public partial class App : System.Windows.Application
{
    public const string MinimizedArgument = "--minimized";



    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public DesktopPaths Paths { get; } = DesktopPaths.Default;
    public DesktopSettings Settings { get; private set; } = new();
    public DesktopServer? Server { get; private set; }

    public static new App Current => (App)System.Windows.Application.Current;

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
        // 想定外のエラーは画面を落とさずに記録する（詳細はログ）
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            _window?.ShowError($"予期しないエラーが起きました（{args.Exception.Message}）。");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };
        _tray = new TrayIcon(this);
        _window = new MainWindow(this);
        if (!e.Args.Contains(MinimizedArgument)) _window.Show();
        _ = StartServerAsync();
    }

    /// <summary>アプリ（Web 版と同じ処理）をプロセス内で起動して画面を開く。失敗したら理由とやり直しのボタンを表示する。</summary>
    public async Task StartServerAsync()
    {
        _window?.ShowStatus("起動しています…");
        try
        {
            if (Server is not null) await Server.DisposeAsync();
            Server = new DesktopServer(new DesktopServerOptions(Paths, Settings.Port,
                Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe")));
            Server.StoppedUnexpectedly += () => Dispatcher.Invoke(OnServerStopped);
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

    private void OnServerStopped()
    {
        if (_exiting) return;
        _window?.ShowError("ReachForge の処理が停止しました。予約投稿は止まっています。");
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

    /// <summary>終了する。保存中の処理を終えてから止める。</summary>
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

    private void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Paths.Logs, "desktop-errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void OpenExternal(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows のサインアウト・シャットダウン：処理を止めてから終わる
        _exiting = true;
        Task.Run(() => Server?.StopAsync() ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(10));
        base.OnSessionEnding(e);
    }
}
