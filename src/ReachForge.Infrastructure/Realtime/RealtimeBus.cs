using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using StackExchange.Redis;

namespace ReachForge.Infrastructure.Realtime;

/// <summary>
/// プロセス内の出来事の配信（画面・SignalR Hub が購読する）。購読側の失敗は発行側へ伝えない。
/// </summary>
public sealed class RealtimeBus(ILogger<RealtimeBus> log)
{
    private ImmutableArray<Func<RealtimeEvent, Task>> _handlers = [];

    public IDisposable Subscribe(Func<RealtimeEvent, Task> handler)
    {
        ImmutableInterlocked.Update(ref _handlers, h => h.Add(handler));
        return new Subscription(() => ImmutableInterlocked.Update(ref _handlers, h => h.Remove(handler)));
    }

    public void Deliver(RealtimeEvent e)
    {
        foreach (var handler in _handlers)
        {
            _ = InvokeAsync(handler, e);
        }
    }

    private async Task InvokeAsync(Func<RealtimeEvent, Task> handler, RealtimeEvent e)
    {
        try
        {
            await handler(e);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Realtime handler failed for {Event}", e.GetType().Name);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}

/// <summary>単一プロセス（開発・Worker:RunInWeb）：同じプロセスの画面へ届ける。</summary>
public sealed class LocalRealtimeNotifier(RealtimeBus bus) : IRealtimeNotifier
{
    public void Publish(RealtimeEvent e) => bus.Deliver(e);
}

/// <summary>
/// 複数プロセス（Web と Worker、複数の Web インスタンス）：Redis の Pub/Sub で全プロセスへ届ける。
/// 自分が出したものは Redis から戻ってきても二重に配信しない。
/// </summary>
public sealed class RedisRealtimeNotifier(IConnectionMultiplexer redis, RealtimeBus bus, ILogger<RedisRealtimeNotifier> log)
    : IRealtimeNotifier, IHostedService
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("reachforge:realtime");
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly string _origin = Guid.NewGuid().ToString("N");

    private sealed record Envelope(string Origin, RealtimeEvent Event);

    public void Publish(RealtimeEvent e)
    {
        bus.Deliver(e);
        var payload = JsonSerializer.Serialize(new Envelope(_origin, e), s_json);
        _ = redis.GetSubscriber().PublishAsync(Channel, payload, CommandFlags.FireAndForget)
            .ContinueWith(t => log.LogWarning(t.Exception, "Could not publish realtime event"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().SubscribeAsync(Channel, (_, message) =>
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(message.ToString(), s_json);
                if (envelope is not null && envelope.Origin != _origin) bus.Deliver(envelope.Event);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Ignoring malformed realtime message");
            }
        });

    public Task StopAsync(CancellationToken cancellationToken) => redis.GetSubscriber().UnsubscribeAsync(Channel);
}
