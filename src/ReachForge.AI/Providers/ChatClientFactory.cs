using System.Collections.Concurrent;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ReachForge.AI.Providers;

/// <summary>プロバイダ設定から IChatClient を組み立てる（RF-DES-001 付録 A.1）。生成したクライアントは再利用する。</summary>
public interface IChatClientFactory
{
    IChatClient Get(string providerName, AiProviderOptions options, string modelId);
}

public sealed class ChatClientFactory(ILoggerFactory loggerFactory) : IChatClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<(string, string), IChatClient> _clients = new();

    public IChatClient Get(string providerName, AiProviderOptions options, string modelId) =>
        _clients.GetOrAdd((providerName, modelId), _ => Build(options, modelId));

    private IChatClient Build(AiProviderOptions options, string modelId)
    {
        IChatClient inner = options.Type switch
        {
            AiProviderType.Stub => new StubChatClient(),
            AiProviderType.OpenAI => new OpenAI.Chat.ChatClient(modelId, options.ApiKey).AsIChatClient(),
            AiProviderType.Anthropic => new AnthropicClient { ApiKey = options.ApiKey! }.AsIChatClient(modelId),
            _ => throw new NotSupportedException($"Unknown AI provider type {options.Type}"),
        };

        // 機密データ（プロンプト本文）はテレメトリに出さない（EnableSensitiveData = false）
        return inner.AsBuilder()
            .UseOpenTelemetry(loggerFactory, sourceName: "ReachForge.AI", configure: o => o.EnableSensitiveData = false)
            .UseLogging(loggerFactory)
            .Build();
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
    }
}
