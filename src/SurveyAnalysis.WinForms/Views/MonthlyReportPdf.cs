using System;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SurveyAnalysis.Reports;

namespace SurveyAnalysis.WinForms;

// Renders an executive-facing 月次レポート to a PDF with QuestPDF: a header (company / title / month), the
// three KPI cards, the sentiment split and the topic breakdown. Japanese text uses Yu Gothic UI (the app
// font, installed on Windows). The Community license is enabled in Program.cs.
internal static class MonthlyReportPdf
{
    // Palette mirrors the app (accent #005FB8; sentiment colours match the dashboard).
    private const string Ink = "#0F172A";
    private const string Muted = "#64748B";
    private const string Accent = "#005FB8";
    private const string CardBg = "#F1F5F9";
    private const string Border = "#E2E8F0";
    private const string Positive = "#16A34A";
    private const string Neutral = "#CA8A04";
    private const string Negative = "#DC2626";

    public static void Save(MonthlyReportData data, string path) =>
        Document.Create(container => Compose(container, data)).GeneratePdf(path);

    private static void Compose(IDocumentContainer container, MonthlyReportData data)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(t => t.FontFamily("Yu Gothic UI").FontSize(10).FontColor(Ink));
            page.Header().Element(h => Header(h, data));
            page.Content().PaddingVertical(18).Element(c => Body(c, data));
            page.Footer().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                t.Span("アンケート分析 — ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    private static void Header(IContainer container, MonthlyReportData data)
    {
        container.BorderBottom(2).BorderColor(Accent).PaddingBottom(10).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                if (!string.IsNullOrWhiteSpace(data.CompanyName))
                    col.Item().Text(data.CompanyName).FontSize(11).FontColor(Muted);
                col.Item().Text("顧客満足度 月次レポート").FontSize(20).Bold().FontColor(Ink);
                col.Item().PaddingTop(2).Text($"{data.ProjectName} ・ {data.MonthLabel}").FontSize(11).FontColor(Accent);
            });
            row.ConstantItem(120).AlignRight().AlignBottom().Text($"対象月 {data.MonthLabel}").FontSize(10).FontColor(Muted);
        });
    }

    private static void Body(IContainer container, MonthlyReportData data)
    {
        container.Column(col =>
        {
            col.Spacing(20);
            col.Item().Element(c => KpiRow(c, data));
            col.Item().Element(c => Section(c, "感情極性の分布", sc => SentimentBars(sc, data)));
            col.Item().Element(c => Section(c, "トピック別 件数", tc => TopicBars(tc, data)));
        });
    }

    private static void KpiRow(IContainer container, MonthlyReportData data)
    {
        container.Row(row =>
        {
            row.Spacing(12);
            Kpi(row.RelativeItem(), "総回答数", data.TotalResponses.ToString(), "件", Ink);
            Kpi(row.RelativeItem(), "ネガティブ件数", data.AnalysedResponses > 0 ? data.NegativeCount.ToString() : "—", "要対応 件", Negative);
            Kpi(row.RelativeItem(), "平均感情スコア", data.AverageSentiment, "-1.0 〜 +1.0", Accent);
        });
    }

    private static void Kpi(IContainer container, string label, string value, string note, string valueColor)
    {
        container.Background(CardBg).Border(1).BorderColor(Border).Padding(14).Column(col =>
        {
            col.Item().Text(label).FontSize(9).FontColor(Muted);
            col.Item().PaddingTop(4).Text(value).FontSize(24).Bold().FontColor(valueColor);
            col.Item().Text(note).FontSize(8).FontColor(Muted);
        });
    }

    private static void Section(IContainer container, string title, Action<IContainer> content)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(12).Bold().FontColor(Ink);
            col.Item().PaddingTop(8).Element(content);
        });
    }

    private static void SentimentBars(IContainer container, MonthlyReportData data)
    {
        if (data.AnalysedResponses == 0)
        {
            container.Text("感情分析が未実行のため表示できません。").FontSize(9).FontColor(Muted);
            return;
        }
        var colors = new[] { Positive, Neutral, Negative };
        var max = Math.Max(1, data.SentimentDistribution.Max(d => d.Count));
        container.Column(col =>
        {
            col.Spacing(6);
            for (var i = 0; i < data.SentimentDistribution.Count; i++)
            {
                var (label, count) = data.SentimentDistribution[i];
                var color = colors[i % colors.Length];
                col.Item().Element(e => Bar(e, label, count, max, color));
            }
        });
    }

    private static void TopicBars(IContainer container, MonthlyReportData data)
    {
        if (data.TopicCounts.Count == 0)
        {
            container.Text(data.AnalysedResponses == 0 ? "感情分析が未実行のため表示できません。" : "割り当てられたトピックがありません。").FontSize(9).FontColor(Muted);
            return;
        }
        var max = Math.Max(1, data.TopicCounts.Max(d => d.Count));
        container.Column(col =>
        {
            col.Spacing(6);
            foreach (var (topic, count) in data.TopicCounts)
                col.Item().Element(e => Bar(e, topic, count, max, Accent));
        });
    }

    // One labelled horizontal bar: the label (fixed left), the bar (proportional width), the count (right).
    private static void Bar(IContainer container, string label, int count, int max, string color)
    {
        container.Row(row =>
        {
            row.ConstantItem(140).Text(label).FontSize(9).FontColor(Ink);
            row.RelativeItem().AlignMiddle().Row(barRow =>
            {
                var filled = Math.Max(0.01f, (float)count / max);
                barRow.RelativeItem(filled).Height(12).Background(color);
                var rest = 1f - filled;
                if (rest > 0.001f)
                    barRow.RelativeItem(rest).Height(12);
            });
            row.ConstantItem(36).AlignRight().Text(count.ToString()).FontSize(9).FontColor(Muted);
        });
    }
}
