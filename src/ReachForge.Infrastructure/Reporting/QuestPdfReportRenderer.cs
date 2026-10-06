using System.Globalization;
using Microsoft.Extensions.Options;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Platforms;

namespace ReachForge.Infrastructure.Reporting;

public sealed class ReportOptions
{
    public const string SectionName = "Reports";

    /// <summary>
    /// 日本語を表示できるフォント（優先順）。コンテナに日本語フォントがない場合は <see cref="FontPath"/> でフォントファイルを指定する。
    /// </summary>
    public string[] FontFamilies { get; set; } = ["Noto Sans JP", "Noto Sans CJK JP", "IPAexGothic", "IPAGothic", "Yu Gothic", "Meiryo"];

    /// <summary>追加で読み込むフォントファイル（.ttf / .otf）またはフォルダ。</summary>
    public string? FontPath { get; set; }

    /// <summary>OS にインストールされたフォントも使う（QuestPDF 2026.9 以降は既定で無効）。</summary>
    public bool UseSystemFonts { get; set; } = true;
}

/// <summary>
/// レポートの PDF 化（QuestPDF）。A4 縦：表紙情報 → 3行まとめ → KPI → SNS別 → 上位・下位投稿 → 良かった点／課題／次の施策。
/// AI の文章には根拠の数値（ファクト）を併記する。グラフは色だけに頼らないよう数値ラベル付きの棒で描く。
/// </summary>
public sealed class QuestPdfReportRenderer : IReportPdfRenderer
{
    private const string Ink = "#1F2937";
    private const string Muted = "#6B7280";
    private const string Accent = "#0A5BD6";
    private const string Ai = "#6D28D9";
    private readonly string[] _fonts;

    public QuestPdfReportRenderer(IOptions<ReportOptions> options)
    {
        // Community License：年商100万USD未満の営利企業・非営利団体・OSS は無償（README 参照）
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = options.Value.UseSystemFonts;
        // 候補のフォントのうち見つかったものを使う。日本語の字形がないまま出力しないよう、欠けた字形はエラーにする
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        if (options.Value.FontPath is { Length: > 0 } path)
        {
            if (Directory.Exists(path)) FontManager.RegisterFontsFromDirectory(path);
            else if (File.Exists(path)) FontManager.RegisterFontFromFile(path);
        }
        _fonts = options.Value.FontFamilies;
    }

    public byte[] Render(ReportDocument d) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(36);
        page.DefaultTextStyle(t => t.FontFamily(_fonts).FontSize(10).FontColor(Ink).LineHeight(1.4f));

        page.Header().PaddingBottom(12).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(Clean(d.Title)).FontSize(16).Bold();
                col.Item().Text(Clean($"{d.BrandName}　{d.PeriodLabel}")).FontColor(Muted);
            });
            if (d.Logo is { Length: > 0 } logo)
            {
                row.ConstantItem(90).Height(40).AlignRight().Image(logo).FitArea();
            }
        });

        page.Content().Column(col =>
        {
            col.Spacing(14);
            col.Item().Element(c => AiSection(c, "3行まとめ", d.Insight.Summary, d.Facts));
            col.Item().Element(c => Kpis(c, d.Data));
            if (d.Data.Platforms.Count > 0) col.Item().Element(c => Platforms(c, d.Data));
            if (d.Data.Ranking.Count > 0) col.Item().Element(c => Ranking(c, d.Data));
            if (d.Insight.Good.Count > 0) col.Item().Element(c => AiSection(c, "良かった点", d.Insight.Good, d.Facts));
            if (d.Insight.Issues.Count > 0) col.Item().Element(c => AiSection(c, "課題", d.Insight.Issues, d.Facts));
            if (d.Insight.NextActions.Count > 0) col.Item().Element(c => AiSection(c, "次の施策", d.Insight.NextActions, d.Facts));
            col.Item().Text(t =>
            {
                t.Span("数値はシステムが計算した値です。文章は AI が作成し、引用した数値が集計結果と一致することを確認しています。")
                    .FontSize(8).FontColor(Muted);
                if (d.ModelLabel is not null) t.Span($"（AI：{d.ModelLabel}）").FontSize(8).FontColor(Muted);
                t.Span("SNSのデータは最大24時間遅れることがあります。").FontSize(8).FontColor(Muted);
            });
        });

        page.Footer().Row(row =>
        {
            row.RelativeItem().Text($"作成：{d.GeneratedAtLabel}　ReachForge").FontSize(8).FontColor(Muted);
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.CurrentPageNumber().FontSize(8);
                t.Span(" / ").FontSize(8);
                t.TotalPages().FontSize(8);
            });
        });
    })).GeneratePdf();

    private static void AiSection(IContainer c, string title, IReadOnlyList<ReportClaim> claims, IReadOnlyDictionary<string, ReportFact> facts) =>
        c.Border(1).BorderColor("#DDD6FE").Background("#F5F3FF").Padding(10).Column(col =>
        {
            col.Spacing(4);
            col.Item().Text($"◆ {title}（AI）").Bold().FontColor(Ai);
            foreach (var claim in claims)
            {
                col.Item().Text(t =>
                {
                    t.Span("・" + Clean(claim.Text));
                    var evidence = claim.FactIds.Select(id => facts.GetValueOrDefault(id)).OfType<ReportFact>()
                        .Select(f => Clean($"{f.Label}：{f.Value}")).ToList();
                    if (evidence.Count > 0) t.Span($"　［根拠］{string.Join("／", evidence)}").FontSize(8).FontColor(Muted);
                });
            }
        });

    private static void Kpis(IContainer c, AnalyticsData data) => c.Column(col =>
    {
        col.Item().PaddingBottom(4).Text("主な指標").Bold().FontSize(12);
        col.Item().Row(row =>
        {
            row.Spacing(8);
            foreach (var k in data.Kpis)
            {
                row.RelativeItem().Border(1).BorderColor("#E5E7EB").Padding(8).Column(k1 =>
                {
                    k1.Item().Text(k.Label).FontSize(8).FontColor(Muted);
                    k1.Item().Text(k.Value.ToString(k.Format, CultureInfo.InvariantCulture)).FontSize(14).Bold();
                    if (k.Change is { } ch)
                    {
                        k1.Item().Text($"{(ch >= 0 ? "▲" : "▼")} {Math.Abs(ch).ToString("P1", CultureInfo.InvariantCulture)} 比較期間比")
                            .FontSize(8).FontColor(ch >= 0 ? "#047857" : "#B91C1C");
                    }
                });
            }
        });
    });

    private static void Platforms(IContainer c, AnalyticsData data) => c.Column(col =>
    {
        col.Item().PaddingBottom(4).Text("SNS別の表示回数と反応の割合").Bold().FontSize(12);
        var max = Math.Max(1, data.Platforms.Max(p => p.Impressions));
        foreach (var p in data.Platforms)
        {
            col.Item().PaddingVertical(2).Row(row =>
            {
                row.ConstantItem(90).Text(PlatformCatalog.Get(p.Platform).DisplayName);
                row.RelativeItem().Row(bar =>
                {
                    var ratio = (float)p.Impressions / max;
                    if (ratio > 0) bar.RelativeItem(ratio).Height(12).Background(Accent);
                    if (ratio < 1) bar.RelativeItem(1 - ratio).Height(12);
                });
                row.ConstantItem(140).AlignRight().Text(
                    $"{p.Impressions.ToString("N0", CultureInfo.InvariantCulture)} 回／{p.EngagementRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "—"}");
            });
        }
    });

    private static void Ranking(IContainer c, AnalyticsData data) => c.Column(col =>
    {
        col.Item().PaddingBottom(4).Text("投稿ランキング（反応の割合が高い順、上位5件と最下位）").Bold().FontSize(12);
        var rows = data.Ranking.Take(5).Select((p, i) => (Rank: (i + 1).ToString(CultureInfo.InvariantCulture), Post: p)).ToList();
        if (data.Ranking.Count > 5) rows.Add(("最下位", data.Ranking[^1]));
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(40);
                cols.RelativeColumn(4);
                cols.RelativeColumn(2);
                cols.RelativeColumn(2);
                cols.RelativeColumn(2);
            });
            table.Header(h =>
            {
                foreach (var title in new[] { "順位", "件名", "SNS", "表示回数", "反応の割合" })
                {
                    h.Cell().BorderBottom(1).BorderColor("#9CA3AF").PaddingVertical(3).Text(title).Bold().FontSize(9);
                }
            });
            foreach (var (rank, p) in rows)
            {
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).Text(rank);
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).Text(Clean(p.IsAiGenerated ? $"{p.Title}（AI）" : p.Title));
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).Text(PlatformCatalog.Get(p.Platform).DisplayName);
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).AlignRight()
                    .Text(p.Impressions.ToString("N0", CultureInfo.InvariantCulture));
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).AlignRight()
                    .Text(p.EngagementRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "—");
            }
        });
    });

    /// <summary>
    /// 日本語フォントにない字形（絵文字・装飾記号）を除く。AI の文章や投稿の件名に絵文字が含まれても PDF を作れるようにする。
    /// </summary>
    internal static string Clean(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsSurrogate(ch)) continue;                       // 絵文字など（BMP 外）
            if (ch is >= '\u2600' and <= '\u27BF') continue;         // その他の記号・装飾記号（☕ ✨ など）
            if (ch is '\uFE0F' or '\uFE0E' or '\u200D') continue;   // 異体字セレクタ・ZWJ
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
