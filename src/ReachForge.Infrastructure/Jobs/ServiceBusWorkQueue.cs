using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>
/// Azure Service Bus のキュー（Jobs:Queue = ServiceBus）。トレース（Diagnostic-Id）は SDK がメッセージに付けて引き継ぐ。
/// </summary>
public sealed class ServiceBusWorkQueue(ServiceBusClient client) : IWorkQueue, IDeadLetterAdmin, IAsyncDisposable
{
    public const int MaxRequeue = 1000;

    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    private ServiceBusSender Sender(string queue) => _senders.GetOrAdd(queue, client.CreateSender);

    public Task EnqueueAsync(string queue, string payload, CancellationToken ct) =>
        Sender(queue).SendMessageAsync(new ServiceBusMessage(payload)
        {
            MessageId = Guid.CreateVersion7().ToString(),
            ContentType = payload.StartsWith('{') ? "application/json" : "text/plain",
        }, ct);

    public async Task<IReadOnlyList<DeadLetter>> PeekAsync(string queue, int max, CancellationToken ct)
    {
        await using var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var messages = await receiver.PeekMessagesAsync(max, cancellationToken: ct);
        return messages.Select(m => new DeadLetter(queue, m.MessageId, m.Body.ToString(),
            m.DeadLetterErrorDescription ?? m.DeadLetterReason, m.EnqueuedTime)).ToList();
    }

    public async Task<int> RequeueAsync(string queue, CancellationToken ct)
    {
        await using var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var sender = Sender(queue);
        var count = 0;
        while (count < MaxRequeue)
        {
            var batch = await receiver.ReceiveMessagesAsync(50, TimeSpan.FromSeconds(2), ct);
            if (batch.Count == 0) break;
            foreach (var message in batch)
            {
                var copy = new ServiceBusMessage(message);
                copy.ApplicationProperties.Remove("DeadLetterReason");
                copy.ApplicationProperties.Remove("DeadLetterErrorDescription");
                await sender.SendMessageAsync(copy, ct);
                await receiver.CompleteMessageAsync(message, ct);
                count++;
            }
        }
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values) await sender.DisposeAsync();
    }
}

/// <summary>
/// Service Bus のメッセージを処理する。失敗したメッセージは手放して再配信させ、5回目の失敗でデッドレターへ移す（14章）。
/// </summary>
public sealed class ServiceBusWorkConsumer(
    ServiceBusClient client,
    IEnumerable<IWorkHandler> handlers,
    IOptions<JobOptions> options,
    ILogger<ServiceBusWorkConsumer> log) : IHostedService, IAsyncDisposable
{
    private readonly List<ServiceBusProcessor> _processors = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var handler in handlers.GroupBy(h => h.Queue).Select(g => g.First()))
        {
            var processor = client.CreateProcessor(handler.Queue, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = Math.Max(1, options.Value.ServiceBus.MaxConcurrentCalls),
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30), // 動画生成などの長い処理でもロックを保つ
            });
            processor.ProcessMessageAsync += args => HandleAsync(handler, args);
            processor.ProcessErrorAsync += args =>
            {
                log.LogError(args.Exception, "Service Bus error on {Queue} ({Source})", args.EntityPath, args.ErrorSource);
                return Task.CompletedTask;
            };
            await processor.StartProcessingAsync(cancellationToken);
            _processors.Add(processor);
        }
    }

    private async Task HandleAsync(IWorkHandler handler, ProcessMessageEventArgs args)
    {
        try
        {
            await handler.HandleAsync(args.Message.Body.ToString(), args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            if (args.Message.DeliveryCount >= WorkQueues.MaxDeliveryCount)
            {
                log.LogError(ex, "{Queue} message {MessageId} moved to dead-letter after {Attempts} attempts",
                    handler.Queue, args.Message.MessageId, args.Message.DeliveryCount);
                var reason = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                await args.DeadLetterMessageAsync(args.Message, "ProcessingFailed", reason, CancellationToken.None);
            }
            else
            {
                log.LogWarning(ex, "{Queue} message {MessageId} failed (attempt {Attempt})", handler.Queue, args.Message.MessageId,
                    args.Message.DeliveryCount);
                await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in _processors) await processor.StopProcessingAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var processor in _processors) await processor.DisposeAsync();
        _processors.Clear();
    }
}

/// <summary>Jobs:ServiceBus:CreateQueues が有効なら、起動時にキューを作る（最大配信回数 5、期限切れもデッドレターへ）。</summary>
public sealed class ServiceBusQueueInitializer(ServiceBusAdministrationClient admin, ILogger<ServiceBusQueueInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in WorkQueues.All)
        {
            if (await admin.QueueExistsAsync(queue, cancellationToken)) continue;
            await admin.CreateQueueAsync(new CreateQueueOptions(queue)
            {
                MaxDeliveryCount = WorkQueues.MaxDeliveryCount,
                DeadLetteringOnMessageExpiration = true,
                LockDuration = TimeSpan.FromMinutes(5),
                DefaultMessageTimeToLive = TimeSpan.FromDays(7),
            }, cancellationToken);
            log.LogInformation("Created Service Bus queue {Queue}", queue);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
