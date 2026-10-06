using Microsoft.Extensions.AI;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Routing;

/// <summary>
/// AI 呼び出しに付随する情報。<see cref="ChatOptions.AdditionalProperties"/> に載せてパイプラインへ渡す
/// （計量・テレメトリ・ローカル用スタブが参照する）。
/// </summary>
public sealed record AiCallContext(AiTaskType Task, Guid? GenerationId = null, object? Payload = null)
{
    public const string Key = "rf.context";

    public static AiCallContext? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out var value) == true ? value as AiCallContext : null;

    public ChatOptions Apply(ChatOptions? options = null)
    {
        options ??= new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[Key] = this;
        return options;
    }
}
