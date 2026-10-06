using System.Threading.Channels;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Social.Webhooks;

namespace ReachForge.Web.Hosting;

/// <summary>署名検証済みの Webhook（SNS へはすぐ 200 を返し、取り込み・分類は後で行う）。</summary>
public sealed record WebhookWork(string Source, string Body, Guid? ChannelId = null);

/// <summary>
/// Webhook の処理待ち（14章 WebhookProcessJob）。初期実装はプロセス内のキューで、複数インスタンス・再起動時の取りこぼしは
/// ポーリング（InboxPollJob）で補う。大量処理時は Service Bus（再試行5回→DLQ）へ移行する。
/// </summary>
public sealed class WebhookQueue
{
    private readonly Channel<WebhookWork> _channel = Channel.CreateBounded<WebhookWork>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public bool TryEnqueue(WebhookWork work) => _channel.Writer.TryWrite(work);

    public IAsyncEnumerable<WebhookWork> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class WebhookProcessor(WebhookQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<WebhookProcessor> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(scopes, work, clock.GetUtcNow(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Webhook processing failed ({Source})", work.Source);
            }
        }
    }

    public static async Task<int> ProcessAsync(IServiceScopeFactory scopes, WebhookWork work, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current =
            new MutableTenantContext { IsSystem = true, UserName = "webhook" };
        var inbox = scope.ServiceProvider.GetRequiredService<InboxService>();
        if (work.Source == "line" && work.ChannelId is { } channelId)
        {
            return await inbox.IngestForChannelAsync(channelId, WebhookParsers.ParseLine(work.Body, now), ct);
        }
        var events = work.Source == "threads" ? WebhookParsers.ParseThreads(work.Body, now) : WebhookParsers.ParseMeta(work.Body, now);
        var total = 0;
        foreach (var group in events.GroupBy(e => (e.Platform, e.AccountId)))
        {
            total += await inbox.IngestWebhookAsync(group.Key.Platform, group.Key.AccountId, group.Select(e => e.Item).ToList(), ct);
        }
        return total;
    }
}
