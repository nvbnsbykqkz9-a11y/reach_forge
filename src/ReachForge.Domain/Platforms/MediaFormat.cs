namespace ReachForge.Domain.Platforms;

/// <summary>画像・動画の向き。生成 AI のキービジュアルと動画は、向きごとに1つずつつくり、SNS のサイズに切り出す。</summary>
public enum MediaOrientation : short
{
    Square = 1,
    /// <summary>縦長（4:5・9:16 など）。</summary>
    Portrait = 2,
    /// <summary>横長（16:9・1.91:1 など）。</summary>
    Landscape = 3,
}

/// <summary>
/// SNS に投稿・入稿する画像や動画の形式（用途・比率・大きさ）。値は 2026年10月時点の各社のヘルプと主要な解説の調査値。
/// </summary>
/// <param name="Key">ファイル名などに使う識別子（例：feed）。</param>
/// <param name="Label">画面に出す用途（例：フィード）。</param>
/// <param name="MaxSeconds">動画の長さの上限（秒）。</param>
public sealed record MediaFormat(string Key, string Label, AspectRatio Aspect, int Width, int Height, int? MaxSeconds = null)
{
    public MediaOrientation Orientation => Aspect.Value switch
    {
        > 1.05 => MediaOrientation.Landscape,
        < 0.95 => MediaOrientation.Portrait,
        _ => MediaOrientation.Square,
    };

    /// <summary>「フィード（4:5・1080×1350）」の形。</summary>
    public string Description => $"{Label}（{Aspect}・{Width}×{Height}）";

    public static string OrientationLabel(MediaOrientation o) => o switch
    {
        MediaOrientation.Portrait => "縦型",
        MediaOrientation.Landscape => "横型",
        _ => "正方形",
    };
}
