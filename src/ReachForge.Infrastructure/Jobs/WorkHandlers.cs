using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Infrastructure.Hosting;
using ReachForge.Social.Webhooks;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>署名検証済みの Webhook（SNS へはすぐ 200 を返し、取り込み・分類は後で行う）。</summary>
public sealed record WebhookWork(string Source, string Body, Guid? ChannelId = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    public static WebhookWork FromJson(string json) =>
        JsonSerializer.Deserialize<WebhookWork>(json, JsonSerializerOptions.Web) ?? throw new FormatException("Empty webhook message");
}

/// <summary>
/// WebhookProcessJob（14章：受信イベントの分類・格納・炎上検知）。重複配信は取り込み時に（チャネル＋SNS 上の ID）で排除するため、
/// 再試行しても二重に取り込まれない。
/// </summary>
public sealed class WebhookWorkHandler(IServiceScopeFactory scopes, TimeProvider clock) : IWorkHandler
{
    public string Queue => WorkQueues.Webhooks;

    public Task HandleAsync(string payload, CancellationToken ct) => ProcessAsync(WebhookWork.FromJson(payload), ct);

    public async Task<int> ProcessAsync(WebhookWork work, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
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

/// <summary>AiGenerationJob（14章）。本文は AiJob の ID。ジョブの状態で二重実行を防ぐため、再配信されても1回だけ実行される。</summary>
public sealed class AiJobWorkHandler(IServiceScopeFactory scopes) : IWorkHandler
{
    public string Queue => WorkQueues.AiJobs;

    public Task HandleAsync(string payload, CancellationToken ct) =>
        Guid.TryParse(payload, out var id) ? AiJobDispatcher.RunJobAsync(scopes, id, ct) : Task.CompletedTask;
}
