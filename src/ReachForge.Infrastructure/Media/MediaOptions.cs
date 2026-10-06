namespace ReachForge.Infrastructure.Media;

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>ローカル保存先（Blob を使わない場合）。Web と Worker で同じ場所を指すこと。</summary>
    public string LocalPath { get; set; } = "media.local";

    /// <summary>Azure Blob Storage（例：https://&lt;account&gt;.blob.core.windows.net）。設定時は Blob に保存し SAS で配信する。</summary>
    public string? BlobServiceUri { get; set; }
    public string BlobContainer { get; set; } = "media";

    /// <summary>
    /// アプリ経由で配信する場合の公開 URL（例：https://app.example.com）。SNS（Instagram 等）が画像を取得するため、
    /// Blob を使わない環境で実投稿するには、インターネットから到達できる HTTPS の URL を設定する。
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>文字入れに使う日本語フォントのファイル（.ttf/.otf）。未設定なら OS のフォントから探す。</summary>
    public string? FontPath { get; set; }

    public string[] FontFamilies { get; set; } = ["Noto Sans JP", "Noto Sans CJK JP", "IPAexGothic", "IPAGothic", "Yu Gothic", "Meiryo"];
}

public sealed class ContentSafetyOptions
{
    public const string SectionName = "ContentSafety";
    public string? Endpoint { get; set; }
    public string? Key { get; set; }

    /// <summary>この重大度以上をブロックする（0〜7、既定 4）。</summary>
    public int BlockSeverity { get; set; } = 4;
    public string ApiVersion { get; set; } = "2024-09-01";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Key);
}
