namespace ReachForge.Infrastructure.Help;

/// <summary>アプリの情報（About ダイアログ・操作説明書に表示する）。</summary>
public static class AppInfo
{
    public const string Name = "ReachForge";
    public const string Subtitle = "LP から SNS の広告・動画をつくる";
    public const string Version = "1.00.00";
    public const string DisplayVersion = "ver " + Version;
    public const string Developer = "株式会社TechnologyFrontier";
    public const string DeveloperUrl = "https://www.technologyfrontier.co.jp";
    public const string ContactEmail = "info@technologyfrontier.co.jp";
    public const string Copyright = "© 2026 " + Developer;

    /// <summary>アプリのアイコン（PNG・256×256）。</summary>
    public static byte[] Icon()
    {
        using var stream = typeof(AppInfo).Assembly.GetManifestResourceStream("ReachForge.Help.icon.png")
                           ?? throw new InvalidOperationException("アイコンのリソースがありません。");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
