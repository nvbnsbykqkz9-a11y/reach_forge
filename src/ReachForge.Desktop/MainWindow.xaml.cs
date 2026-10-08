using System;
using System.Linq;
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
/// ダウンロード（画像・動画・まとめての ZIP）は「名前を付けて保存」で保存先を選び、アプリが受け取って保存する。
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
            core.DownloadStarting += OnDownloadStarting;
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
            using var http = CreateHttpClient(TimeSpan.FromSeconds(60));
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

    /// <summary>このアプリのサーバーにつなぐ HttpClient（このアプリの証明書だけを信頼する）。</summary>
    private HttpClient CreateHttpClient(TimeSpan timeout, string? cookie = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == SslPolicyErrors.None || (cert is not null && _server?.IsOwnCertificate(cert) == true),
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        if (!string.IsNullOrEmpty(cookie)) http.DefaultRequestHeaders.Add("Cookie", cookie);
        return http;
    }

    /// <summary>
    /// ダウンロード：アプリの中のファイルは、WebView2 のダウンロードを止めて「名前を付けて保存」で保存先を選んでもらい、
    /// ログイン中のクッキーを付けてアプリが受け取って保存する（WebView2 の表示に頼らず、終わったこと・失敗した理由を知らせる）。
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var url = e.DownloadOperation.Uri;
        if (!IsOwnOrigin(url)) return; // 外部サイトのダウンロードは WebView2 に任せる
        e.Cancel = true;
        e.Handled = true;
        var suggested = Path.GetFileName(e.ResultFilePath);
        _ = SaveDownloadAsync(new Uri(url), string.IsNullOrWhiteSpace(suggested) ? "ReachForge" : suggested);
    }

    private async Task SaveDownloadAsync(Uri url, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "名前を付けて保存",
            FileName = suggestedName,
            InitialDirectory = DownloadsFolder(),
            Filter = Path.GetExtension(suggestedName) is { Length: > 1 } ext
                ? $"{ext.TrimStart('.').ToUpperInvariant()} ファイル (*{ext})|*{ext}|すべてのファイル (*.*)|*.*"
                : "すべてのファイル (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var previous = Cursor;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(url.ToString());
            var cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            using var http = CreateHttpClient(TimeSpan.FromMinutes(10), cookie);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show(this, $"ダウンロードできませんでした（{(int)response.StatusCode} {response.ReasonPhrase}）。画面を開き直してから、もう一度お試しください。" +
                    "続く場合は、トレイの「ログを開く」で記録を確認してください。", "ReachForge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var temp = dialog.FileName + ".download";
            await using (var target = File.Create(temp))
            {
                await response.Content.CopyToAsync(target);
            }
            File.Move(temp, dialog.FileName, overwrite: true);
            Cursor = previous;
            if (MessageBox.Show(this, $"保存しました。\n{dialog.FileName}\n\n保存したフォルダーを開きますか？", "ReachForge",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                App.ShowInExplorer(dialog.FileName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"ダウンロードできませんでした（{ex.Message}）。保存先のフォルダーに書き込めるか、空き容量があるかを確認してください。",
                "ReachForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = previous;
        }
    }

    private static string DownloadsFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
