using ReachForge.Domain.Analytics;

namespace ReachForge.Application.Ai;

/// <summary>上位・下位投稿の特徴（形式・時間帯・訴求・長さなど。F-10 処理 2）。値はシステムが抽出する。</summary>
public sealed record PostFeature(string Title, string Platform, string PostedAt, int BodyLength, bool HasImage, string Objective,
    bool IsAiGenerated, string? EngagementFactId);

public sealed record ReportWriterInput(string BrandName, string PeriodLabel, IReadOnlyList<ReportFact> Facts,
    IReadOnlyList<PostFeature> TopPosts, IReadOnlyList<PostFeature> BottomPosts);

/// <summary>Analyst Agent：集計済みの数値から要因分析と次の打ち手を文章にする（数値は計算させない）。</summary>
public interface IReportWriter
{
    Task<(ReportInsight Insight, AiModelInfo Model)> WriteAsync(ReportWriterInput input, CancellationToken ct);
}
