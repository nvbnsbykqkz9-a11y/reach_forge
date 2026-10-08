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
/// ナレーションの音声合成（AiTaskType.Tts のルート）。OpenAI の音声合成（/audio/speech）、ローカル開発は無音のスタブ。
/// 失敗時は次のプロバイダで再試行し、どれも使えなければ理由（キー・残高・モデル・接続など）を添えて知らせる。
/// </summary>
public sealed class TextToSpeechService(IOptions<AiOptions> options, ProviderCircuitBreaker breaker, IAiUsageSink usage,
    IHttpClientFactory http, ILogger<TextToSpeechService> log) : ITextToSpeech
{
    public const string HttpClientName = "ai-speech";

    public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct)
    {
        var o = options.Value;
        var candidates = o.RouteFor(AiTaskType.Tts)
            .Where(n => o.Providers.TryGetValue(n, out var p) && p.IsConfigured && p.Type is AiProviderType.OpenAI or AiProviderType.Stub)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new AiUnavailableException("ナレーションを作れませんでした：音声合成の AI（OpenAI）の API キーが設定されていません。「設定 → 生成AIの設定」で入れてください。");
        }
        Exception? last = null;
        string? reason = null;
        foreach (var name in candidates)
        {
            if (breaker.IsOpen(name))
            {
                reason ??= "続けて失敗したため、音声合成を1分ほど止めています。少し待ってからお試しください。";
                continue;
            }
            var provider = o.Providers[name];
            var model = provider.Type == AiProviderType.Stub ? "stub" : provider.SpeechModel;
            var sw = Stopwatch.StartNew();
            try
            {
                var wav = provider.Type == AiProviderType.Stub
                    ? SilentWav(EstimateSeconds(text))
                    : await OpenAiSpeechAsync(provider, model, text, ct);
                breaker.RecordSuccess(name);
                var seconds = WavSeconds(wav);
                usage.Add(new AiUsageLog
                {
                    TaskType = AiTaskType.Tts, Provider = name, ModelId = model,
                    LatencyMs = (int)sw.ElapsedMilliseconds, Succeeded = true, InputTokens = text.Length,
                    CostUsd = (decimal)seconds / 60m * provider.PricePerSpeechMinute,
                });
                return new SpeechAudio(wav, seconds);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                reason = ex is SpeechFailedException failed ? failed.Reason
                    : ex is HttpRequestException or TaskCanceledException ? "OpenAI に接続できませんでした。インターネットへの接続を確認してください。"
                    : "音声を受け取れませんでした。もう一度お試しください。";
                breaker.RecordFailure(name);
                log.LogWarning(ex, "TTS provider {Provider} ({Model}) failed: {Reason}", name, model, reason);
            }
        }
        throw new AiUnavailableException($"ナレーションを作れませんでした：{reason ?? "もう一度お試しください。"}", last);
    }

    private async Task<byte[]> OpenAiSpeechAsync(AiProviderOptions provider, string model, string text, CancellationToken ct)
    {
        var baseUrl = (provider.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/speech")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                model, input = text, voice = string.IsNullOrWhiteSpace(provider.Voice) ? "alloy" : provider.Voice, response_format = "wav",
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new SpeechFailedException(Describe(response.StatusCode, body, model), $"HTTP {(int)response.StatusCode}: {body}");
    }

    /// <summary>OpenAI のエラーを、利用者が対処できる言葉にする。</summary>
    internal static string Describe(System.Net.HttpStatusCode status, string? body, string model)
    {
        string? code = null, type = null, message = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && System.Text.Json.Nodes.JsonNode.Parse(body)?["error"] is System.Text.Json.Nodes.JsonObject error)
            {
                code = error["code"]?.ToString();
                type = error["type"]?.ToString();
                message = error["message"]?.ToString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        bool Is(string value) => code == value || type == value;
        return (int)status switch
        {
            401 or 403 => "OpenAI の API キーが正しくないか、権限がありません。「設定 → 生成AIの設定」でキーを確認してください。",
            429 when Is("insufficient_quota") => "OpenAI の残高（クレジット）が足りません。OpenAI の管理画面で支払い方法と残高を確認してください。",
            429 => "OpenAI の利用の上限に達したか、混み合っています。少し時間をおいてお試しください。",
            400 or 404 when Is("model_not_found") || message?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                $"音声合成のモデル「{(model.Length == 0 ? "（空欄）" : model)}」を使えません。設定の AI:Providers:openai:TaskModels:Tts を確認してください（既定は {AiProviderOptions.DefaultSpeechModel}）。",
            >= 500 => "OpenAI 側で障害が起きています。時間をおいてお試しください。",
            _ => $"OpenAI から音声を受け取れませんでした（HTTP {(int)status}{(message is null ? "" : $"：{message}")}）。",
        };
    }

    /// <summary>OpenAI が音声合成を断った（Reason は画面に出す言葉）。</summary>
    private sealed class SpeechFailedException(string reason, string detail) : Exception(detail)
    {
        public string Reason { get; } = reason;
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
