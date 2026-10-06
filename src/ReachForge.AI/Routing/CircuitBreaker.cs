using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ReachForge.AI.Routing;

/// <summary>プロバイダ単位のサーキットブレーカー（プロセス内で共有するシングルトン）。</summary>
public sealed class ProviderCircuitBreaker(IOptions<AiOptions> options, TimeProvider clock)
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil;
    }

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
        }
    }

    public void RecordFailure(string provider)
    {
        var o = options.Value.CircuitBreaker;
        var s = _states.GetOrAdd(provider, _ => new State());
        lock (s)
        {
            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= o.FailureThreshold)
            {
                s.OpenUntil = clock.GetUtcNow().AddSeconds(o.BreakSeconds);
                s.ConsecutiveFailures = 0;
            }
        }
    }
}
