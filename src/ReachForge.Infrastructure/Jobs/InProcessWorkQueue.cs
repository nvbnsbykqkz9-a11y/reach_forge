using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// プロセス内のキュー（Jobs:Queue = InProcess）。このプロセスに処理役（<see cref="IWorkHandler"/>）がいるキューだけを受け付け、
/// いないキューへの登録は捨てる（AI ジョブは DB に保存済みで、処理するプロセスの巡回が拾う）。
/// 再起動で中身は失われる。5回失敗したメッセージはメモリ上のデッドレターに残す。
/// </summary>
public sealed class InProcessWorkQueue : IWorkQueue, IDeadLetterAdmin
{
    public const int Capacity = 1000;
    public const int DeadLetterCapacity = 500;

    internal sealed record Envelope(string Id, string Payload, DateTimeOffset EnqueuedAt);

    private readonly Dictionary<string, Channel<Envelope>> _channels;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DeadLetter>> _dead = new();
    private readonly TimeProvider _clock;

    public InProcessWorkQueue(IEnumerable<IWorkHandler> handlers, TimeProvider clock)
    {
        _clock = clock;
        _channels = handlers.Select(h => h.Queue).Distinct().ToDictionary(q => q, _ =>
            Channel.CreateBounded<Envelope>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest }));
    }

    public Task EnqueueAsync(string queue, string payload, CancellationToken ct)
    {
        if (_channels.TryGetValue(queue, out var channel))
        {
            channel.Writer.TryWrite(new Envelope(Guid.CreateVersion7().ToString(), payload, _clock.GetUtcNow()));
        }
        return Task.CompletedTask;
    }

    internal IEnumerable<(string Queue, ChannelReader<Envelope> Reader)> Readers => _channels.Select(c => (c.Key, c.Value.Reader));

    internal void DeadLetter(string queue, Envelope message, string reason)
    {
        var list = _dead.GetOrAdd(queue, _ => new ConcurrentQueue<DeadLetter>());
        list.Enqueue(new DeadLetter(queue, message.Id, message.Payload, reason, message.EnqueuedAt));
        while (list.Count > DeadLetterCapacity && list.TryDequeue(out _))
        {
        }
    }

    public Task<IReadOnlyList<DeadLetter>> PeekAsync(string queue, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DeadLetter>>(_dead.TryGetValue(queue, out var list) ? list.Take(max).ToList() : []);

    public async Task<int> RequeueAsync(string queue, CancellationToken ct)
    {
        if (!_dead.TryGetValue(queue, out var list)) return 0;
        var count = 0;
        while (list.TryDequeue(out var dead))
        {
            await EnqueueAsync(queue, dead.Payload, ct);
            count++;
        }
        return count;
    }
}

/// <summary>プロセス内キューの処理ループ。失敗したら間隔をあけて最大5回まで試し、だめならデッドレターに移す。</summary>
public sealed class InProcessWorkConsumer(
    InProcessWorkQueue queue,
    IEnumerable<IWorkHandler> handlers,
    TimeProvider clock,
    ILogger<InProcessWorkConsumer> log) : BackgroundService
{
    public static TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var byQueue = handlers.GroupBy(h => h.Queue).ToDictionary(g => g.Key, g => g.First());
        return Task.WhenAll(queue.Readers.Select(r => LoopAsync(r.Queue, r.Reader, byQueue[r.Queue], stoppingToken)));
    }

    private async Task LoopAsync(string name, System.Threading.Channels.ChannelReader<InProcessWorkQueue.Envelope> reader,
        IWorkHandler handler, CancellationToken ct)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(ct))
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var activity = SystemJobRunner.Source.StartActivity($"{name} process");
                        await handler.HandleAsync(message.Payload, ct);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        if (attempt >= WorkQueues.MaxDeliveryCount)
                        {
                            log.LogError(ex, "{Queue} message {MessageId} moved to dead-letter after {Attempts} attempts", name, message.Id, attempt);
                            queue.DeadLetter(name, message, ex.Message);
                            break;
                        }
                        log.LogWarning(ex, "{Queue} message {MessageId} failed (attempt {Attempt})", name, message.Id, attempt);
                        await Task.Delay(BaseRetryDelay * Math.Pow(2, attempt - 1), clock, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
