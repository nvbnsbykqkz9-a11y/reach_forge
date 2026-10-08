using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ReachForge.AI.Routing;

/// <summary>
/// サーキットブレーカー（プロセス内で共有するシングルトン）。続けて失敗したプロバイダを、しばらく使わない。
/// 文章はプロバイダ名、画像・動画・音声合成は <see cref="Key"/>（プロバイダ名と機能）ごとに数える。
/// 同じ会社でも機能ごとに止める（例：OpenAI の画像生成が続けて失敗しても、OpenAI の音声合成は止めない）。
/// </summary>
public sealed class ProviderCircuitBreaker(IOptions<AiOptions> options, TimeProvider clock)
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil;
        public string? LastFailure;
    }

    public const string Image = "image";
    public const string Video = "video";
    public const string Speech = "tts";

    /// <summary>機能ごとの数え方の名前（例：openai:tts）。</summary>
    public static string Key(string provider, string capability) => $"{provider}:{capability}";

    /// <summary>止めるきっかけになった最後の失敗の理由（利用者向けの言葉。記録がなければ null）。</summary>
    public string? LastFailure(string provider) => _states.TryGetValue(provider, out var s) ? s.LastFailure : null;

    private readonly ConcurrentDictionary<string, State> _states = new();

    public bool IsOpen(string provider) =>
        _states.TryGetValue(provider, out var s) && s.OpenUntil > clock.GetUtcNow();

    public void RecordSuccess(string provider)
    {
        var s = _states.GetOrAdd(provider, _ => new State());
        lock (s)
        {
            s.ConsecutiveFailures = 0;
            s.OpenUntil = default;
            s.LastFailure = null;
        }
    }

    public void RecordFailure(string provider, string? reason = null)
    {
        var o = options.Value.CircuitBreaker;
        var s = _states.GetOrAdd(provider, _ => new State());
        lock (s)
        {
            if (reason is not null) s.LastFailure = reason;
            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= o.FailureThreshold)
            {
                s.OpenUntil = clock.GetUtcNow().AddSeconds(o.BreakSeconds);
                s.ConsecutiveFailures = 0;
            }
        }
    }
}
