namespace ReachForge.Desktop.Host;

/// <summary>
/// サーバー（ReachForge.Web）の場所を探す。
/// 1) 環境変数 REACHFORGE_SERVER_PATH（デバッグで別のビルドを使うとき）
/// 2) 発行した形：アプリと同じフォルダーの server\ReachForge.Web.exe
/// 3) 開発中（Visual Studio で F5）：リポジトリの src\ReachForge.Web\bin\{構成}\net10.0 のビルド結果
/// </summary>
public static class ServerLocator
{
    public const string EnvironmentVariable = "REACHFORGE_SERVER_PATH";

    public static string Find(string appDirectory, string? configuration = null, Func<string, string?>? env = null)
    {
        if ((env ?? Environment.GetEnvironmentVariable)(EnvironmentVariable) is { Length: > 0 } configured) return configured;

        var bundled = Path.Combine(appDirectory, "server", "ReachForge.Web.exe");
        if (File.Exists(bundled)) return bundled;

        for (var dir = new DirectoryInfo(appDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReachForge.sln"))) continue;
            var output = Path.Combine(dir.FullName, "src", "ReachForge.Web", "bin", configuration ?? "Debug", "net10.0");
            foreach (var name in new[] { "ReachForge.Web.exe", "ReachForge.Web.dll" })
            {
                if (File.Exists(Path.Combine(output, name))) return Path.Combine(output, name);
            }
            break;
        }
        return bundled; // 見つからなければ発行した形の場所（起動時に「見つかりません」と案内する）
    }
}
