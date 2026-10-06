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
