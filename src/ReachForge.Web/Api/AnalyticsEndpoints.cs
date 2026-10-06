using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Web.Api;

/// <summary>分析・AI レポート（F-10：GET /analytics、POST /reports）。</summary>
public static class AnalyticsEndpoints
{
    public sealed record CreateReportRequest(DateTimeOffset From, DateTimeOffset To, SocialPlatform? Platform, Guid? CampaignId,
        CompareMode Compare = CompareMode.PreviousPeriod);

    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/analytics", (DateTimeOffset from, DateTimeOffset to, SocialPlatform? platform, Guid? campaignId, CompareMode? compare,
            AnalyticsService s, CancellationToken ct) =>
            s.BuildAsync(new AnalyticsFilter(from, to, platform, campaignId, compare ?? CompareMode.PreviousPeriod), ct));

        api.MapGet("/analytics/posts.csv", async (DateTimeOffset from, DateTimeOffset to, SocialPlatform? platform, Guid? campaignId,
            AnalyticsService s, CancellationToken ct) =>
            Results.File(await s.CsvAsync(new AnalyticsFilter(from, to, platform, campaignId), ct), "text/csv; charset=utf-8",
                $"posts-{from:yyyyMMdd}-{to:yyyyMMdd}.csv"));

        api.MapPost("/reports", async (CreateReportRequest r, ReportService s, CancellationToken ct) =>
        {
            var (report, job) = await s.RequestAsync(new ReportRequest(r.From, r.To, ReportKind.Custom, r.Platform, r.CampaignId, r.Compare), ct);
            return Results.Accepted($"/api/v1/reports/{report.Id}", new { reportId = report.Id, jobId = job.Id });
        });
        api.MapGet("/reports", async (ReportService s, CancellationToken ct) =>
            (await s.ListAsync(50, ct)).Select(r => new { r.Id, r.Title, r.Kind, r.Status, r.PeriodFrom, r.PeriodTo, r.CreatedAt, hasPdf = r.PdfPath != null }));
        api.MapGet("/reports/{id:guid}", async (Guid id, ReportService s, CancellationToken ct) =>
        {
            var v = await s.GetAsync(id, ct);
            return new
            {
                v.Report.Id, v.Report.Title, v.Report.Kind, v.Report.Status, v.Report.PeriodFrom, v.Report.PeriodTo,
                v.Report.RejectedClaims, v.Report.ModelId, v.Report.Error, v.Insight, facts = v.Facts.Values, v.Data,
            };
        });
        api.MapGet("/reports/{id:guid}/pdf", async (Guid id, ReportService s, CancellationToken ct) =>
        {
            var (content, name) = await s.OpenPdfAsync(id, ct);
            return Results.File(content, "application/pdf", name);
        });
        api.MapGet("/reports/settings", (ReportService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        api.MapPut("/reports/settings", async (ReportSettings body, ReportService s, CancellationToken ct) =>
        {
            await s.UpdateSettingsAsync(body, ct);
            return Results.NoContent();
        });
    }
}
