using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.AI.Services;

/// <summary>構成台本（F-05 ③ 処理 1）。</summary>
public sealed class VideoScriptWriter(IModelRouter router, IPromptCatalog prompts) : IVideoScriptWriter
{
    public async Task<VideoScript> WriteAsync(BrandContext brand, string theme, int sceneCount, int targetSeconds, CancellationToken ct)
    {
        var client = router.Resolve(AiTaskType.Video);
        var options = new AiCallContext(AiTaskType.Video, null, new ScriptStubPayload(theme, sceneCount, targetSeconds, brand.Profile.BrandName)).Apply();
        var (draft, _) = await CopyGenerationService.GetStructuredAsync<ScriptDraft>(client,
            [new(ChatRole.System, (await prompts.RenderAsync(PromptKeys.VideoScript, PromptLibrary.ScriptValues(brand, sceneCount, targetSeconds), ct)).Text), new(ChatRole.User, PromptInjectionDetector.Fence(theme))],
            options, ct);
        var scenes = (draft.Scenes ?? []).Take(sceneCount)
            .Select(s => new VideoScene(s.Caption?.Trim() ?? "", s.Narration?.Trim() ?? "", Math.Clamp(s.Seconds, 2, 8)))
            .ToList();
        // 出力ガードレール：NG 語・規制表現を含むテロップ／ナレーションは使わない
        var guard = brand.ToGuardrailContext();
        if (scenes.Any(s => GuardrailChecker.CheckContent($"{s.Caption}\n{s.Narration}", guard).Level == GuardrailLevel.Error))
        {
            throw new AiSafetyBlockedException("ブランドのNGワード・規制表現");
        }
        return new VideoScript(string.IsNullOrWhiteSpace(draft.Title) ? theme : draft.Title.Trim(), scenes);
    }
}

/// <summary>
/// ナレーションの音声合成（AiTaskType.Tts のルート）。OpenAI の音声合成、ローカル開発は無音のスタブ。
/// 失敗時は次のプロバイダで再試行する。
/// </summary>
public sealed class TextToSpeechService(IOptions<AiOptions> options, ProviderCircuitBreaker breaker, IAiUsageSink usage,
    ILogger<TextToSpeechService> log) : ITextToSpeech
{
    public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct)
    {
        var o = options.Value;
        var candidates = o.RouteFor(AiTaskType.Tts)
            .Where(n => o.Providers.TryGetValue(n, out var p) && p.IsConfigured && p.Type is AiProviderType.OpenAI or AiProviderType.Stub)
            .ToList();
        if (candidates.Count == 0) throw new AiUnavailableException("音声合成のAIが設定されていません。");
        Exception? last = null;
        foreach (var name in candidates)
        {
            if (breaker.IsOpen(name)) continue;
            var provider = o.Providers[name];
            var sw = Stopwatch.StartNew();
            try
            {
                var wav = provider.Type == AiProviderType.Stub
                    ? SilentWav(EstimateSeconds(text))
                    : (await new OpenAI.Audio.AudioClient(provider.ModelFor(AiTaskType.Tts) ?? "gpt-4o-mini-tts", provider.ApiKey)
                        .GenerateSpeechAsync(text, new OpenAI.Audio.GeneratedSpeechVoice(provider.Voice ?? "alloy"),
                            new OpenAI.Audio.SpeechGenerationOptions { ResponseFormat = OpenAI.Audio.GeneratedSpeechFormat.Wav }, ct)).Value.ToArray();
                breaker.RecordSuccess(name);
                var seconds = WavSeconds(wav);
                usage.Add(new AiUsageLog
                {
                    TaskType = AiTaskType.Tts, Provider = name, ModelId = provider.ModelFor(AiTaskType.Tts) ?? "",
                    LatencyMs = (int)sw.ElapsedMilliseconds, Succeeded = true, InputTokens = text.Length,
                    CostUsd = (decimal)seconds / 60m * provider.PricePerSpeechMinute,
                });
                return new SpeechAudio(wav, seconds);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                breaker.RecordFailure(name);
                log.LogWarning(ex, "TTS provider {Provider} failed", name);
            }
        }
        throw new AiUnavailableException("ナレーションを作れませんでした。もう一度お試しください。", last);
    }

    /// <summary>日本語の読み上げはおよそ1秒に7文字。</summary>
    internal static double EstimateSeconds(string text) => Math.Clamp(text.Length / 7.0, 1, 15);

    /// <summary>16bit・モノラル・24kHz の無音 WAV（ローカル用）。</summary>
    internal static byte[] SilentWav(double seconds)
    {
        const int rate = 24000;
        var samples = (int)(rate * seconds);
        var data = samples * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    /// <summary>WAV の長さ（秒）。fmt と data チャンクから求める。読めなければ 0。</summary>
    internal static double WavSeconds(byte[] wav)
    {
        if (wav.Length < 44) return 0;
        int byteRate = 0, dataSize = 0;
        for (var i = 12; i + 8 <= wav.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, i, 4);
            var size = BitConverter.ToInt32(wav, i + 4);
            if (id == "fmt " && i + 16 <= wav.Length) byteRate = BitConverter.ToInt32(wav, i + 16);
            if (id == "data") { dataSize = size is <= 0 or int.MaxValue ? wav.Length - i - 8 : size; break; }
            i += 8 + Math.Max(0, size) + (size % 2);
        }
        return byteRate > 0 ? (double)dataSize / byteRate : 0;
    }
}
