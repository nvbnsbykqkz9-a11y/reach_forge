using System.Text.Json;

namespace ReachForge.Desktop.Host;

/// <summary>
/// Windows 版のデータフォルダー（既定 %LOCALAPPDATA%\ReachForge）。DB・メディア・ログ・証明書・設定を置く。
/// アプリを入れ替えて（更新して）もここは残る。
/// </summary>
public sealed class DesktopPaths(string root)
{
    public static DesktopPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReachForge"));

    public string Root { get; } = root;
    public string Database => Path.Combine(Root, "data", "reachforge.db");
    public string Media => Path.Combine(Root, "media");
    public string Logs => Path.Combine(Root, "logs");
    public string Certificate => Path.Combine(Root, "certs", "localhost.pfx");
    public string Settings => Path.Combine(Root, "desktop.json");

    /// <summary>利用者が編集する設定（API キー・SNS アプリの設定など）。アプリが起動時に読み込む（変更は再起動で反映）。</summary>
    public string UserConfig => Path.Combine(Root, "appsettings.user.json");

    /// <summary>WebView2 のプロファイル（Cookie など）。</summary>
    public string WebView => Path.Combine(Root, "webview");

    public DesktopPaths EnsureCreated()
    {
        foreach (var dir in new[] { Root, Path.GetDirectoryName(Database)!, Media, Logs, Path.GetDirectoryName(Certificate)!, WebView })
        {
            Directory.CreateDirectory(dir);
        }
        if (!File.Exists(UserConfig)) File.WriteAllText(UserConfig, UserConfigTemplate);
        return this;
    }

    /// <summary>初回に作る設定ファイルのひな形（値が空の項目は使われない）。</summary>
    public const string UserConfigTemplate = """
        {
          "Social": { "UseMock": false },
          "AI": {
            "Providers": {
              "anthropic": { "ApiKey": "" },
              "openai": { "ApiKey": "" },
              "google": { "ApiKey": "" }
            }
          },
          "Social": {
            "X": { "ClientId": "", "ClientSecret": "" },
            "Meta": { "AppId": "", "AppSecret": "" },
            "Threads": { "AppId": "", "AppSecret": "" },
            "TikTok": { "ClientKey": "", "ClientSecret": "", "Audited": false },
            "YouTube": { "ClientId": "", "ClientSecret": "" }
          }
        }
        """;
}

/// <summary>デスクトップアプリ自体の設定（desktop.json）。</summary>
public sealed record DesktopSettings
{
    /// <summary>
    /// アプリが待ち受けるポート（https://localhost:{Port}）。SNS アプリに登録するコールバック URL に含まれるため固定にする。
    /// </summary>
    public int Port { get; init; } = 47120;

    /// <summary>ウィンドウを閉じてもタスクトレイに残り、予約投稿を続ける。</summary>
    public bool KeepRunningInTray { get; init; } = true;

    /// <summary>トレイに残ったことを一度知らせたか。</summary>
    public bool TrayNoticeShown { get; init; }

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public static DesktopSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), s_json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new(); // 壊れていれば既定値で起動する
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, s_json));
}
