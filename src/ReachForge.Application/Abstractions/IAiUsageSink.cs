using ReachForge.Domain.Entities;

namespace ReachForge.Application.Abstractions;

/// <summary>
/// AI 呼び出しの計量の一時置き場（要求スコープ・スレッドセーフ）。
/// 並列の AI 呼び出しから記録し、DbContext の保存時にまとめて ai_usage_log へ書き込む。
/// </summary>
public interface IAiUsageSink
{
    void Add(AiUsageLog log);
    IReadOnlyList<AiUsageLog> Drain();
}
