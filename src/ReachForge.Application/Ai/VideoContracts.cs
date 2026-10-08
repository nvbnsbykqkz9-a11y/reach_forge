using ReachForge.Application.Services;

namespace ReachForge.Application.Ai;

/// <summary>動画の1シーン（テロップ・ナレーション・秒数）。</summary>
public sealed record VideoScene(string Caption, string Narration, double Seconds);

public sealed record VideoScript(string Title, IReadOnlyList<VideoScene> Scenes);

/// <summary>構成台本（F-05 ③ 処理 1：15〜30秒、シーンごとのテロップ・ナレーション・秒数を JSON で）。</summary>
public interface IVideoScriptWriter
{
    Task<VideoScript> WriteAsync(BrandContext brand, string theme, int sceneCount, int targetSeconds, CancellationToken ct);
}

/// <summary>音声合成の結果（PCM の WAV）。</summary>
public sealed record SpeechAudio(byte[] Wav, double Seconds);

/// <summary>ナレーションの音声合成（TTS）。声のクローン（実在人物の声の再現）は扱わない。</summary>
public interface ITextToSpeech
{
    Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct);
}

/// <summary>生成 AI による動画（F-05 ① テキスト→動画、② 画像→動画）の指定。</summary>
public sealed record VideoGenerationSpec
{
    public required string Prompt { get; init; }

    /// <summary>長さ（秒）。プロバイダが対応する長さのうち近いものにする。</summary>
    public int Seconds { get; init; } = 8;

    /// <summary>縦型（9:16、既定）または横型（16:9）。</summary>
    public (int Width, int Height) Size { get; init; } = (1080, 1920);

    /// <summary>② 画像→動画の起点の画像（<see cref="Size"/> と同じ比率に整えたもの）。</summary>
    public byte[]? StartImage { get; init; }
    public string? StartImageMime { get; init; }

    /// <summary>使うプロバイダ（動画の品質テスト用。指定すると代替プロバイダへは切り替えず、失敗の理由をそのまま返す）。</summary>
    public string? Provider { get; init; }

    /// <summary>モデル（品質テスト用。空なら設定のモデル）。</summary>
    public string? Model { get; init; }

    /// <summary>描かないものの指定（空なら既定：文字・ロゴ・人物の顔など）。</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>高画質モード（Kling の pro：1080p・時間と料金が多くかかる）。</summary>
    public bool HighQuality { get; init; }

    /// <summary>動画生成 AI に既定で渡す「描かないもの」（文字・ロゴは崩れやすく、人物の顔は肖像権のため）。</summary>
    public const string DefaultNegativePrompt =
        "text, letters, captions, subtitles, logos, watermarks, distorted UI, garbled screens, people's faces, low quality, flicker";

    public const int MinSeconds = 4;
    public const int MaxSeconds = 12;
    public const int MaxPromptLength = 1000;
}

public sealed record GeneratedVideo(byte[] Mp4, AiModelInfo Model);

/// <summary>生成 AI の動画（モデルルータ経由。失敗時は代替プロバイダへ、全体で 15 分を超えたら失敗：F-05 例外）。</summary>
public interface IVideoGenerationService
{
    Task<GeneratedVideo> GenerateAsync(VideoGenerationSpec spec, Guid? generationId, CancellationToken ct);

    /// <summary>動画生成に使えるプロバイダ（ルートの順）。API キーが設定済みか・既定のモデル・1秒あたりの単価（USD）。</summary>
    IReadOnlyList<VideoProviderInfo> Providers();
}

/// <summary>動画生成のプロバイダ（名前・種類・設定済みか・既定のモデル・1秒あたりの単価）。</summary>
public sealed record VideoProviderInfo(string Name, string Type, bool Configured, string DefaultModel, decimal PricePerSecond);

/// <summary>LP 動画の1シーン。<paramref name="ImageIndex"/> は使う LP の画像の番号（0 始まり、なければ null）。</summary>
/// <remarks><paramref name="Points"/>：シーンで見せる短い言葉（困りごと・良さ・機能など。動画の図解に使う）。</remarks>
public sealed record LandingPageScene(string Role, string Caption, string Narration, double Seconds, int? ImageIndex)
{
    public IReadOnlyList<string> Points { get; init; } = [];

    public const int MaxPoints = 4;
    public const int MaxPointLength = 14;
}

/// <summary>
/// LP（ランディングページ）から作る集客動画の企画（訴求の整理＋絵コンテ）。
/// 価格・数値・効果は LP に書かれていることだけを使う（生成後に LP の本文と突き合わせる）。
/// </summary>
public sealed record LandingPageVideoPlan(
    string Title,
    string Product,
    string Target,
    IReadOnlyList<string> Benefits,
    string Offer,
    string CallToAction,
    IReadOnlyList<LandingPageScene> Scenes,
    string PostText,
    IReadOnlyList<string> Hashtags,
    string HookMotion);

/// <summary>LP の内容から、縦型ショート動画の企画と絵コンテをつくる（構造化出力）。</summary>
public interface ILandingPageVideoPlanner
{
    Task<LandingPageVideoPlan> PlanAsync(BrandContext brand, WebPage page, int sceneCount, int targetSeconds, CancellationToken ct);
}
