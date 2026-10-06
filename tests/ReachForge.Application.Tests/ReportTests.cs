using System.Text;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

public class ReportTests
{
    private static async Task RunJobAsync(AppFixture f, Guid jobId)
    {
        await using var scope = f.Scope();
        await f.Get<AiJobProcessor>(scope).ProcessAsync(jobId, CancellationToken.None);
    }

    [Fact]
    public async Task Analytics_computes_kpis_breakdown_and_facts()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var now = f.Clock.GetUtcNow();
        var data = await f.Get<AnalyticsService>(scope).BuildAsync(new AnalyticsFilter(now.AddDays(-30), now), CancellationToken.None);

        Assert.Equal(4, data.Kpis.Count);
        Assert.NotEmpty(data.Ranking);
        Assert.Equal(data.Ranking.Count, data.Platforms.Sum(p => p.Posts));
        Assert.InRange(data.Trend.Count, 30, 31); // 期間の両端の日を含む（テナントのタイムゾーン）
        Assert.Equal(7, data.HeatMap.Length);
        Assert.True(data.Kpis.Single(k => k.Label == "フォロワー増加").Value > 0); // 日次のフォロワー数から計算
        var facts = AnalyticsService.Facts(data);
        Assert.Contains(facts, x => x.Label == "表示回数");
        Assert.Equal(facts.Count, facts.Select(x => x.Id).Distinct().Count());

        // SNS で絞り込み
        var x = await f.Get<AnalyticsService>(scope).BuildAsync(
            new AnalyticsFilter(now.AddDays(-30), now, SocialPlatform.Instagram), CancellationToken.None);
        Assert.All(x.Ranking, p => Assert.Equal(SocialPlatform.Instagram, p.Platform));

        var csv = Encoding.UTF8.GetString(await f.Get<AnalyticsService>(scope).CsvAsync(new AnalyticsFilter(now.AddDays(-30), now),
            CancellationToken.None));
        Assert.StartsWith("﻿公開日時,SNS,件名", csv);
    }

    [Fact]
    public async Task Report_job_writes_verified_insight_and_pdf()
    {
        await using var f = await AppFixture.CreateAsync();
        var now = f.Clock.GetUtcNow();
        Guid reportId, jobId;
        await using (var scope = f.Scope())
        {
            var (report, job) = await f.Get<ReportService>(scope).RequestAsync(new ReportRequest(now.AddDays(-30), now), CancellationToken.None);
            (reportId, jobId) = (report.Id, job.Id);
            Assert.Equal(0, job.CreditsHeld); // レポートはクレジットを消費しない
        }
        await RunJobAsync(f, jobId);
        Assert.True(f.Logs.Errors.IsEmpty, string.Join("\n", f.Logs.Errors));

        await using (var scope = f.Scope())
        {
            var reports = f.Get<ReportService>(scope);
            var view = await reports.GetAsync(reportId, CancellationToken.None);
            Assert.True(view.Report.Status == ReportStatus.Succeeded, $"{view.Report.ErrorCode} {view.Report.Error}");
            Assert.Equal(3, view.Insight.Summary.Count);
            Assert.Equal(3, view.Insight.NextActions.Count);
            Assert.Equal(0, view.Report.RejectedClaims);
            Assert.All(view.Insight.Summary, c => Assert.All(c.FactIds, id => Assert.True(view.Facts.ContainsKey(id))));
            Assert.NotNull(view.Data);

            var (pdf, name) = await reports.OpenPdfAsync(reportId, CancellationToken.None);
            await using (pdf)
            {
                var head = new byte[4];
                await pdf.ReadExactlyAsync(head);
                Assert.Equal("%PDF", Encoding.ASCII.GetString(head));
            }
            Assert.EndsWith(".pdf", name);
            var job = await f.Get<IAppDbContext>(scope).AiJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(AiJobStatus.Succeeded, job.Status);
        }
    }

    [Fact]
    public async Task Pdf_renders_text_with_emoji()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var now = f.Clock.GetUtcNow();
        var data = await f.Get<AnalyticsService>(scope).BuildAsync(new AnalyticsFilter(now.AddDays(-30), now), CancellationToken.None);
        var facts = AnalyticsService.Facts(data).ToDictionary(x => x.Id);
        var insight = new ReachForge.Domain.Analytics.ReportInsight(
            [new("ラテが人気でした☕✨😊", [facts.Keys.First()])], [], [], []);
        var pdf = f.Get<IReportPdfRenderer>(scope).Render(new ReportDocument("月次レポート🎉", "ほっこりカフェ☕", "2026/9/1〜9/30",
            "2026/10/01 07:00", null, data, insight, facts, "stub-local"));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task Weekly_report_is_scheduled_once_after_monday_7am_local()
    {
        await using var f = await AppFixture.CreateAsync();
        await using (var scope = f.Scope())
        {
            await f.Get<ReportService>(scope).UpdateSettingsAsync(new ReportSettings { Weekly = true, Recipients = ["boss@example.com"] },
                CancellationToken.None);
            await Assert.ThrowsAsync<Domain.Common.DomainException>(() => f.Get<ReportService>(scope)
                .UpdateSettingsAsync(new ReportSettings { Recipients = ["not-an-email"] }, CancellationToken.None));
        }

        // 2026-10-06 00:00Z は火曜 09:00 JST → 先週分（9/28〜10/5）を作成する
        int Run()
        {
            using var scope = f.Scope(c => c.IsSystem = true);
            return f.Get<ReportService>(scope).ScheduleDueAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        Assert.Equal(1, Run());
        Assert.Equal(0, Run()); // 冪等

        await using (var scope = f.Scope())
        {
            var report = await f.Get<IAppDbContext>(scope).Reports.SingleAsync();
            Assert.Equal(ReportKind.Weekly, report.Kind);
            Assert.Equal(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.FromHours(9)), report.PeriodFrom);
            Assert.Equal(DemoSeeder.WorkspaceId, report.WorkspaceId);
        }
    }
}
