using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record ReportRequest(DateTimeOffset From, DateTimeOffset To, ReportKind Kind = ReportKind.Custom,
    SocialPlatform? Platform = null, Guid? CampaignId = null, CompareMode Compare = CompareMode.PreviousPeriod);

internal sealed record ReportJobRequest(Guid ReportId, ReportRequest Request);

/// <summary>保存済みレポートの表示用（DataJson / InsightJson を復元したもの）。</summary>
public sealed record ReportView(Report Report, AnalyticsData? Data, ReportInsight Insight, IReadOnlyDictionary<string, ReportFact> Facts);

/// <summary>
/// AI レポート（F-10）：集計（C#）→ 上位・下位投稿の特徴抽出 → Analyst Agent → 数値の事後検証 → PDF（QuestPDF）→ 配信。
/// 生成は AiJob（TaskType=Report）として Worker で非同期に実行する。クレジットは消費しない。
/// </summary>
public sealed class ReportService(
    IAppDbContext db,
    ITenantContext tenant,
    AnalyticsService analytics,
    SchedulingService scheduling,
    IReportWriter writer,
    IReportPdfRenderer renderer,
    IMediaStorage storage,
    IEmailSender email,
    IWorkQueue queue,
    TimeProvider clock,
    ILogger<ReportService> log)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    /// <summary>レポートの作成を依頼する（画面の「AIレポートを作成」）。</summary>
    public async Task<(Report Report, AiJob Job)> RequestAsync(ReportRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        if (request.To <= request.From || request.To - request.From > MaxPeriod)
        {
            throw new DomainException(ErrorCodes.Validation, "期間は1日以上・1年以内で指定してください。");
        }
        var tz = await scheduling.TenantTimeZoneAsync(ct);
        var result = Enqueue(tenant.TenantId, tenant.WorkspaceId, tenant.UserName, request, tz);
        db.Record(tenant, "report.requested", nameof(Report), result.Report.Id, request.Kind.ToString());
        await db.SaveChangesAsync(ct);
        await queue.NotifyAiJobAsync(result.Job.Id, ct);
        return result;
    }

    public async Task<IReadOnlyList<Report>> ListAsync(int take, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        return (await db.Reports.AsNoTracking().Where(r => r.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt).Take(take).ToList();
    }

    public async Task<ReportView> GetAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == tenant.WorkspaceId, ct)
                     ?? throw new NotFoundException("レポート");
        var data = report.Status == ReportStatus.Succeeded ? JsonSerializer.Deserialize<AnalyticsData>(report.DataJson, s_json) : null;
        var insight = JsonSerializer.Deserialize<ReportInsight>(report.InsightJson, s_json) ?? ReportInsight.Empty;
        var facts = data is null ? new Dictionary<string, ReportFact>() : AnalyticsService.Facts(data).ToDictionary(f => f.Id);
        return new ReportView(report, data, insight with
        {
            Summary = insight.Summary ?? [], Good = insight.Good ?? [], Issues = insight.Issues ?? [], NextActions = insight.NextActions ?? [],
        }, facts);
    }

    public async Task<(Stream Content, string FileName)> OpenPdfAsync(Guid id, CancellationToken ct)
    {
        var view = await GetAsync(id, ct);
        if (view.Report.PdfPath is not { } path) throw new NotFoundException("レポートのPDF");
        return (await storage.OpenReadAsync(path, ct), $"report-{view.Report.PeriodFrom:yyyyMMdd}.pdf");
    }

    public async Task<ReportSettings> GetSettingsAsync(CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        return (await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == tenant.WorkspaceId, ct)).Reports;
    }

    public async Task UpdateSettingsAsync(ReportSettings settings, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageReports);
        var recipients = settings.Recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Any(r => !System.Net.Mail.MailAddress.TryCreate(r, out _)))
        {
            throw new DomainException(ErrorCodes.Validation, "メールアドレスの形式が正しくありません。");
        }
        if (recipients.Count > 20) throw new DomainException(ErrorCodes.Validation, "配信先は20件までです。");
        if (settings.LogoAssetId is { } logo && !await db.MediaAssets.AnyAsync(m => m.Id == logo, ct))
        {
            throw new NotFoundException("ロゴ画像");
        }
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, ct);
        workspace.Reports = new ReportSettings
        {
            Weekly = settings.Weekly, Monthly = settings.Monthly, Recipients = recipients, LogoAssetId = settings.LogoAssetId,
        };
        db.Record(tenant, "report.settings_updated", nameof(Workspace), workspace.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 定期レポートの作成（14章 WeeklyReportJob：月曜 07:00／MonthlyReportJob：毎月1日 07:00、テナントのタイムゾーン）。
    /// システムコンテキストで実行する。同じ期間のレポートがあれば作らない（冪等）。
    /// </summary>
    public async Task<int> ScheduleDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("ScheduleDueAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();
        var tenants = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var workspaces = (await db.Workspaces.AsNoTracking().ToListAsync(ct)).Where(w => w.Reports.Weekly || w.Reports.Monthly).ToList();
        var jobs = new List<Guid>();
        foreach (var w in workspaces)
        {
            if (!tenants.TryGetValue(w.TenantId, out var t)) continue;
            var tz = SchedulingService.FindTimeZone(t.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(now, tz);
            var periods = new List<(ReportKind Kind, DateTimeOffset From, DateTimeOffset To)>();
            if (w.Reports.Weekly)
            {
                var monday = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7));
                if (local >= monday.AddHours(7)) periods.Add((ReportKind.Weekly, Local(monday.AddDays(-7), tz), Local(monday, tz)));
            }
            if (w.Reports.Monthly)
            {
                var first = new DateTime(local.Year, local.Month, 1);
                if (local >= first.AddHours(7)) periods.Add((ReportKind.Monthly, Local(first.AddMonths(-1), tz), Local(first, tz)));
            }
            foreach (var (kind, from, to) in periods)
            {
                if (await db.Reports.AnyAsync(r => r.WorkspaceId == w.Id && r.Kind == kind && r.PeriodFrom == from, ct)) continue;
                jobs.Add(Enqueue(w.TenantId, w.Id, "system", new ReportRequest(from, to, kind), tz).Job.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var id in jobs) await queue.NotifyAiJobAsync(id, ct);
        return jobs.Count;
    }

    /// <summary>レポートを作成する（AiJobProcessor から、依頼したテナント・ワークスペースのコンテキストで呼ぶ）。</summary>
    public async Task ProcessAsync(AiJob job, CancellationToken ct)
    {
        var (reportId, request) = JsonSerializer.Deserialize<ReportJobRequest>(job.RequestJson, s_json)!;
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct) ?? throw new NotFoundException("レポート");
        report.Status = ReportStatus.Running;
        await db.SaveChangesAsync(ct);
        try
        {
            // ① 集計（数値はここで確定し、LLM には計算させない）
            var data = await analytics.BuildAsync(new AnalyticsFilter(request.From, request.To, request.Platform, request.CampaignId, request.Compare), ct);
            var facts = AnalyticsService.Facts(data);
            var factMap = facts.ToDictionary(f => f.Id);
            var tz = await scheduling.TenantTimeZoneAsync(ct);
            var brand = await db.BrandProfiles.AsNoTracking().FirstOrDefaultAsync(b => b.WorkspaceId == report.WorkspaceId, ct);

            // ② 上位・下位投稿の特徴
            var input = new ReportWriterInput(brand?.BrandName ?? "", report.Title, facts,
                data.Ranking.Take(3).Select((p, i) => Feature(p, tz, facts, $"上位{i + 1}位")).ToList(),
                data.Ranking.Count > 3 ? [Feature(data.Ranking[^1], tz, facts, "最下位")] : []);

            // ③ AI の考察 → 事後検証（根拠のない数値を含む主張は除外）
            job.MoveTo(AiJobStage.Generating);
            ReportInsight insight;
            IReadOnlyList<string> rejected = [];
            string model = "";
            if (data.Ranking.Count == 0)
            {
                insight = ReportInsight.Empty; // 投稿がない期間は AI を呼ばない
            }
            else
            {
                var (draft, info) = await writer.WriteAsync(input, ct);
                model = info.ModelId;
                job.MoveTo(AiJobStage.Checking);
                insight = ClaimVerifier.Verify(draft, factMap, out rejected);
                foreach (var r in rejected) log.LogInformation("Report {ReportId}: rejected claim {Reason}", report.Id, r);
            }
            if (insight.Summary.Count == 0) insight = insight with { Summary = FallbackSummary(data, facts) };

            // ④ PDF
            var settings = (await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == report.WorkspaceId, ct)).Reports;
            var logo = await LogoAsync(settings.LogoAssetId ?? brand?.LogoAssetId, ct);
            byte[]? pdf = null;
            string? path = null;
            try
            {
                pdf = renderer.Render(new ReportDocument(report.Title, brand?.BrandName ?? "", PeriodLabel(request.From, request.To, tz),
                    TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture),
                    logo, data, insight, factMap, model.Length == 0 ? null : model));
                path = $"reports/{report.TenantId:N}/{report.Id:N}.pdf";
                await storage.SaveAsync(path, pdf, "application/pdf", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // PDF を作れなくても（フォント未設定など）、レポートは画面で見られるようにする
                log.LogError(ex, "Failed to render PDF for report {ReportId}", report.Id);
                pdf = null;
                path = null;
            }

            report.DataJson = JsonSerializer.Serialize(data, s_json);
            report.InsightJson = JsonSerializer.Serialize(insight, s_json);
            report.RejectedClaims = rejected.Count;
            report.ModelId = model;
            report.PdfPath = path;
            report.Status = ReportStatus.Succeeded;
            report.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            // ⑤ 配信（メール）
            if (pdf is not null && settings.Recipients.Count > 0 && report.Kind != ReportKind.Custom)
            {
                await DeliverAsync(report, insight, pdf, settings.Recipients, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            report.Status = ReportStatus.Failed;
            report.ErrorCode = ex is DomainException d ? d.ErrorCode : ErrorCodes.SysUnexpected;
            report.Error = ex is DomainException ? ex.Message : "レポートを作成できませんでした。もう一度お試しください。";
            report.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task DeliverAsync(Report report, ReportInsight insight, byte[] pdf, IReadOnlyList<string> to, CancellationToken ct)
    {
        try
        {
            var body = string.Join('\n', new[] { $"{report.Title}ができました。", "", "■ 3行まとめ" }
                .Concat(insight.Summary.Select(c => $"・{c.Text}"))
                .Concat(["", "詳しくは添付のPDFをご覧ください。", "", "ReachForge"]));
            await email.SendAsync(new EmailMessage(to, $"【ReachForge】{report.Title}", body,
                [new EmailAttachment($"report-{report.PeriodFrom:yyyyMMdd}.pdf", "application/pdf", pdf)]), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 配信に失敗してもレポート自体は画面から見られるため、失敗にはしない
            log.LogWarning(ex, "Failed to email report {ReportId}", report.Id);
        }
    }

    private Report CreateReport(Guid tenantId, Guid workspaceId, string user, ReportRequest request, TimeZoneInfo tz) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Kind = request.Kind,
        Title = request.Kind switch
        {
            ReportKind.Weekly => $"週次レポート（{PeriodLabel(request.From, request.To, tz)}）",
            ReportKind.Monthly => $"月次レポート（{TimeZoneInfo.ConvertTime(request.From, tz):yyyy年M月}）",
            _ => $"レポート（{PeriodLabel(request.From, request.To, tz)}）",
        },
        PeriodFrom = request.From,
        PeriodTo = request.To,
        RequestedBy = user,
    };

    private (Report Report, AiJob Job) Enqueue(Guid tenantId, Guid workspaceId, string user, ReportRequest request, TimeZoneInfo tz)
    {
        var report = CreateReport(tenantId, workspaceId, user, request, tz);
        var job = new AiJob
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            TaskType = AiTaskType.Report,
            RequestJson = JsonSerializer.Serialize(new ReportJobRequest(report.Id, request), s_json),
            RequestedBy = user,
        };
        report.AiJobId = job.Id;
        db.Reports.Add(report);
        db.AiJobs.Add(job);
        return (report, job);
    }

    private async Task<byte[]?> LogoAsync(Guid? assetId, CancellationToken ct)
    {
        if (assetId is not { } id || await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) is not { } asset) return null;
        try
        {
            await using var stream = await storage.OpenReadAsync(asset.BlobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>AI の考察が使えないとき（全件除外・投稿なし）の、システム計算値だけで作る要約。</summary>
    private static IReadOnlyList<ReportClaim> FallbackSummary(AnalyticsData data, IReadOnlyList<ReportFact> facts)
    {
        if (data.Ranking.Count == 0) return [new ReportClaim("この期間に公開された投稿はありませんでした。", [])];
        var list = new List<ReportClaim>();
        foreach (var label in new[] { "表示回数", "反応の割合", "リンクのクリック" })
        {
            if (facts.FirstOrDefault(f => f.Label == label) is { } f) list.Add(new ReportClaim($"{f.Label}は{f.Value}でした。", [f.Id]));
        }
        return list;
    }

    private static PostFeature Feature(PostPerformance p, TimeZoneInfo tz, IReadOnlyList<ReportFact> facts, string rankLabel)
    {
        var local = TimeZoneInfo.ConvertTime(p.PostedAt, tz);
        return new PostFeature(p.Title, PlatformCatalog.Get(p.Platform).DisplayName,
            $"{DashboardService.DayLabel(local.DayOfWeek)}曜{local.Hour}時", p.BodyLength, p.HasImage, p.Objective.ToLabel(),
            p.IsAiGenerated, facts.FirstOrDefault(f => f.Label.StartsWith(rankLabel, StringComparison.Ordinal))?.Id);
    }

    public static string PeriodLabel(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo tz) =>
        $"{TimeZoneInfo.ConvertTime(from, tz):yyyy/M/d}〜{TimeZoneInfo.ConvertTime(to.AddTicks(-1), tz):M/d}";

    private static DateTimeOffset Local(DateTime localDate, TimeZoneInfo tz) =>
        new(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), tz.GetUtcOffset(localDate));
}
