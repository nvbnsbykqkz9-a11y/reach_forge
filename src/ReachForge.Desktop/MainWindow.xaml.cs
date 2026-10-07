using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ReachForge.Desktop.Host;

namespace ReachForge.Desktop;

/// <summary>
/// 画面（WebView2）。アプリの自己署名証明書だけを例外として受け入れ、外部サイトへのリンクは既定のブラウザーで開く。
/// SNS の連携（OAuth）は同じ画面の中で行い、コールバックで https://localhost に戻る。
/// </summary>
public partial class MainWindow : Window
{
    private readonly App _app;
    private DesktopServer? _server;
    private bool _webViewReady;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
    }

    public async Task OpenAsync(DesktopServer server)
    {
        _server = server;
        if (!_webViewReady)
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _app.Paths.WebView,
                new CoreWebView2EnvironmentOptions { Language = "ja" });
            await Web.EnsureCoreWebView2Async(environment);
            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = true;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.ServerCertificateErrorDetected += OnCertificateError;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrEmpty(core.DocumentTitle) ? "ReachForge" : core.DocumentTitle;
            _webViewReady = true;
        }
        Web.CoreWebView2.Navigate(server.SignInUri.ToString());
        Splash.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
    }

    /// <summary>このアプリの証明書のときだけ許可する（それ以外のサイトの証明書エラーは通常どおり拒否）。</summary>
    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (_server is not null && IsOwnOrigin(e.RequestUri) && _server.IsOwnCertificate(e.ServerCertificate.ToX509Certificate2()))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }
    }

    /// <summary>
    /// ログインの有効期限が切れてログイン画面へ転送されたときは、自動ログインし直す
    /// （自分でログアウトしたときは ReturnUrl が付かないので、ログイン画面のままにする）。
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_server is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsOwnOrigin(e.Uri)) return;
        if (uri.AbsolutePath.Equals("/account/login", StringComparison.OrdinalIgnoreCase)
            && uri.Query.Contains("ReturnUrl=", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            Web.CoreWebView2.Navigate(_server.SignInUri.ToString());
        }
    }

    /// <summary>新しいウィンドウ：アプリ内のページは同じ画面で、外部サイト（投稿の URL など）は既定のブラウザーで開く。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsOwnOrigin(e.Uri)) Web.CoreWebView2.Navigate(e.Uri);
        else if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            App.OpenExternal(uri.ToString());
        }
    }

    private bool IsOwnOrigin(string url) =>
        _server is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Uri.Compare(uri, _server.BaseUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public void ShowStatus(string message)
    {
        Status.Text = message;
        Progress.Visibility = Visibility.Visible;
        ErrorActions.Visibility = Visibility.Collapsed;
        Splash.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed;
    }

    public void ShowError(string message)
    {
        Status.Text = message;
        Progress.Visibility = Visibility.Collapsed;
        ErrorActions.Visibility = Visibility.Visible;
        Splash.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _app.StartServerAsync();

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => App.OpenInExplorer(_app.Paths.Logs);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_app.KeepRunningOnClose())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        _ = _app.ExitAsync();
    }
}
