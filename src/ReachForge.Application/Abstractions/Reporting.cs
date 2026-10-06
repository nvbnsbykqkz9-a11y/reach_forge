using ReachForge.Application.Services;
using ReachForge.Domain.Analytics;

namespace ReachForge.Application.Abstractions;

/// <summary>PDF に組版する内容（数値はシステム計算値、文章は検証済みの AI 考察）。</summary>
public sealed record ReportDocument(
    string Title,
    string BrandName,
    string PeriodLabel,
    string GeneratedAtLabel,
    byte[]? Logo,
    AnalyticsData Data,
    ReportInsight Insight,
    IReadOnlyDictionary<string, ReportFact> Facts,
    string? ModelLabel);

/// <summary>レポートの PDF 化（RF-DES-001 F-10 処理 4：QuestPDF）。</summary>
public interface IReportPdfRenderer
{
    byte[] Render(ReportDocument document);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string TextBody, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>メール送信（レポート配信・招待・通知）。</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
