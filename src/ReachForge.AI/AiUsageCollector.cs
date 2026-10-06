using System.Collections.Concurrent;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;

namespace ReachForge.AI;

public sealed class AiUsageCollector : IAiUsageSink
{
    private readonly ConcurrentQueue<AiUsageLog> _queue = new();

    public void Add(AiUsageLog log) => _queue.Enqueue(log);

    public IReadOnlyList<AiUsageLog> Drain()
    {
        var list = new List<AiUsageLog>();
        while (_queue.TryDequeue(out var log)) list.Add(log);
        return list;
    }
}
