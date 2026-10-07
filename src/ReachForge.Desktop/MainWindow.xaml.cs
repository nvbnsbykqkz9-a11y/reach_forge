using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ReachForge.Desktop.Host;

namespace ReachForge.Desktop;

/// <summary>
/// 画面（WebView2）。アプリの自己署名証明書だけを例外として受け入れ、外部サイトへのリンクは既定のブラウザーで、
/// メールアドレスのリンク（mailto:）は既定のメールアプリで、操作説明書（PDF）は既定の PDF ビューアーで開く。
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
        if (IsMailto(e.Uri))
        {
            e.Cancel = true;
            App.OpenExternal(e.Uri);
            return;
        }
        if (_server is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsOwnOrigin(e.Uri)) return;
        if (uri.AbsolutePath.Equals("/account/login", StringComparison.OrdinalIgnoreCase)
            && uri.Query.Contains("ReturnUrl=", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            Web.CoreWebView2.Navigate(_server.SignInUri.ToString());
        }
    }

    /// <summary>新しいウィンドウ：アプリ内のページは同じ画面で、PDF は既定の PDF ビューアーで、外部サイトは既定のブラウザーで開く。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsMailto(e.Uri)) App.OpenExternal(e.Uri);
        else if (IsOwnOrigin(e.Uri) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var own)
                 && own.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) _ = OpenPdfAsync(own);
        else if (IsOwnOrigin(e.Uri)) Web.CoreWebView2.Navigate(e.Uri);
        else if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            App.OpenExternal(uri.ToString());
        }
    }

    private static bool IsMailto(string url) => url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>アプリの PDF（操作説明書）を一時フォルダーに保存して、既定の PDF ビューアーで開く。</summary>
    private async Task OpenPdfAsync(Uri url)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                // このアプリの証明書だけを信頼する
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    errors == SslPolicyErrors.None || (cert is not null && _server?.IsOwnCertificate(cert) == true),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            var bytes = await http.GetByteArrayAsync(url);
            var path = Path.Combine(Path.GetTempPath(), "ReachForge", Path.GetFileName(url.AbsolutePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            App.OpenExternal(path);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"操作説明書を開けませんでした（{ex.Message}）。", "ReachForge", MessageBoxButton.OK, MessageBoxImage.Warning);
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
