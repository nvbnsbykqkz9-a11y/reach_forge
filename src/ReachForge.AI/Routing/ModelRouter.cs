using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.AI.Providers;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Routing;

/// <summary>タスク種別でモデルを選び、障害時は代替プロバイダへフェイルオーバーする（RF-DES-001 4.2 / 4.3）。</summary>
public interface IModelRouter
{
    IChatClient Resolve(AiTaskType task);
}

public sealed class ConfigDrivenModelRouter(
    IChatClientFactory factory,
    ProviderCircuitBreaker breaker,
    IAiUsageSink usage,
    ITenantContext tenant,
    IOptions<AiOptions> options,
    ILogger<ConfigDrivenModelRouter> log) : IModelRouter
{
    public IChatClient Resolve(AiTaskType task)
    {
        var o = options.Value;
        var candidates = o.RouteFor(task)
            .Where(name => o.Providers.TryGetValue(name, out var p) && p.IsConfigured)
            .Select(name => (Name: name, Options: o.Providers[name]))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new AiUnavailableException("AIサービスが設定されていません。運用管理画面でAIモデル設定を確認してください。");
        }
        return new RoutedChatClient(task, candidates, factory, breaker, usage, tenant, o.MaxOutputTokens, log);
    }
}

/// <summary>候補プロバイダを順に試す IChatClient。成功・失敗とも ai_usage_log に計量を残す。</summary>
internal sealed class RoutedChatClient(
    AiTaskType task,
    IReadOnlyList<(string Name, AiProviderOptions Options)> candidates,
    IChatClientFactory factory,
    ProviderCircuitBreaker breaker,
    IAiUsageSink usage,
    ITenantContext tenant,
    int maxOutputTokens,
    ILogger log) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        Exception? last = null;
        var attempted = 0;
        foreach (var (name, provider) in candidates)
        {
            if (breaker.IsOpen(name)) continue;
            var fallback = attempted++ > 0;
            var modelId = provider.ModelFor(task) ?? "";
            var sw = Stopwatch.StartNew();
            try
            {
                var client = factory.Get(name, provider, modelId);
                var response = await client.GetResponseAsync(list, Prepare(options, modelId), cancellationToken);
                breaker.RecordSuccess(name);
                Record(name, provider, response.ModelId ?? modelId, response.Usage, sw, fallback, succeeded: true, options);
                response.AdditionalProperties ??= [];
                response.AdditionalProperties["rf.provider"] = name;
                response.AdditionalProperties["rf.fallback"] = fallback;
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                breaker.RecordFailure(name);
                Record(name, provider, modelId, null, sw, fallback, succeeded: false, options);
                log.LogWarning(ex, "AI provider {Provider} failed for {Task}; trying next", name, task);
            }
        }
        throw new AiUnavailableException("AIサービスが一時的に利用できません。入力内容は残っています。しばらくしてからもう一度お試しください。", last);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // フェイルオーバーは最初のチャンク受信前のみ。受信開始後の失敗はそのまま呼び出し元へ伝える。
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var attempted = 0;
        foreach (var (name, provider) in candidates)
        {
            if (breaker.IsOpen(name)) continue;
            var fallback = attempted++ > 0;
            var modelId = provider.ModelFor(task) ?? "";
            var sw = Stopwatch.StartNew();
            var client = factory.Get(name, provider, modelId);
            await using var e = client.GetStreamingResponseAsync(list, Prepare(options, modelId), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            bool hasFirst;
            try
            {
                hasFirst = await e.MoveNextAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                breaker.RecordFailure(name);
                Record(name, provider, modelId, null, sw, fallback, succeeded: false, options);
                log.LogWarning(ex, "AI provider {Provider} failed to stream for {Task}; trying next", name, task);
                continue;
            }

            UsageDetails? usageDetails = null;
            if (hasFirst)
            {
                do
                {
                    foreach (var c in e.Current.Contents.OfType<UsageContent>()) usageDetails = c.Details;
                    yield return e.Current;
                } while (await e.MoveNextAsync());
            }
            breaker.RecordSuccess(name);
            Record(name, provider, modelId, usageDetails, sw, fallback, succeeded: true, options);
            yield break;
        }
        throw new AiUnavailableException("AIサービスが一時的に利用できません。しばらくしてからもう一度お試しください。");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private ChatOptions Prepare(ChatOptions? options, string modelId)
    {
        var o = options?.Clone() ?? new ChatOptions();
        if (!string.IsNullOrEmpty(modelId)) o.ModelId = modelId;
        o.MaxOutputTokens ??= maxOutputTokens;
        return o;
    }

    private void Record(string name, AiProviderOptions provider, string modelId, UsageDetails? u, Stopwatch sw,
        bool fallback, bool succeeded, ChatOptions? options)
    {
        var input = u?.InputTokenCount ?? 0;
        var output = u?.OutputTokenCount ?? 0;
        usage.Add(new AiUsageLog
        {
            TenantId = tenant.TenantId,
            AiGenerationId = AiCallContext.From(options)?.GenerationId,
            TaskType = task,
            Provider = name,
            ModelId = modelId,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = (input * provider.InputPricePerMTok + output * provider.OutputPricePerMTok) / 1_000_000m,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            FallbackUsed = fallback,
            Succeeded = succeeded,
        });
    }
}
