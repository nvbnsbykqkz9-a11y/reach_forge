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

    /// <summary>縦型（9:16）。</summary>
    public (int Width, int Height) Size { get; init; } = (1080, 1920);

    /// <summary>② 画像→動画の起点の画像（9:16 に整えたもの）。</summary>
    public byte[]? StartImage { get; init; }
    public string? StartImageMime { get; init; }

    public const int MinSeconds = 4;
    public const int MaxSeconds = 12;
    public const int MaxPromptLength = 1000;
}

public sealed record GeneratedVideo(byte[] Mp4, AiModelInfo Model);

/// <summary>生成 AI の動画（モデルルータ経由。失敗時は代替プロバイダへ、全体で 15 分を超えたら失敗：F-05 例外）。</summary>
public interface IVideoGenerationService
{
    Task<GeneratedVideo> GenerateAsync(VideoGenerationSpec spec, Guid? generationId, CancellationToken ct);
}
