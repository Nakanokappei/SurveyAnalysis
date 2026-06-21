using System;
using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Reports;

// The figures behind a 月次レポート PDF: the project + target month, the KPI numbers, the topic breakdown
// and the sentiment split — all for that calendar month, computed from the analytics star exactly the way
// the dashboard computes them (so the report and the on-screen dashboard never disagree).
public sealed record MonthlyReportData(
    string ProjectName,
    string CompanyName,
    int Year,
    int Month,
    int TotalResponses,
    int AnalysedResponses,
    int NegativeCount,
    string AverageSentiment,
    IReadOnlyList<(string Topic, int Count)> TopicCounts,
    IReadOnlyList<(string Label, int Count)> SentimentDistribution)
{
    public string MonthLabel => $"{Year}年{Month}月";
}

public static class MonthlyReportBuilder
{
    // Builds the report data for one calendar month. The window is the whole month [1日, 末日]; the metrics
    // mirror DashboardViewModel (negative = the LLM flag; positive = score ≥ 0.2; the rest neutral; topic
    // counts group the main 自由記述 topic). companyName is the header company (may be empty).
    public static MonthlyReportData Build(AnalyticsRepository analytics, Project project, string companyName, int year, int month)
    {
        var lastDay = DateTime.DaysInMonth(year, month);
        long fromKey = year * 10000 + month * 100 + 1;
        long toKey = year * 10000 + month * 100 + lastDay;
        var responses = analytics.ResponsesWithAnalysisForScope(project.Id, fromKey, toKey, newestFirst: true);

        var scores = responses.Where(r => r.SentimentScore is not null).Select(r => r.SentimentScore!.Value).ToList();
        var analysed = responses.Count(r => r.SentimentScore is not null || r.Topic is not null);

        // Topic counts (main topic), largest first; responses with no topic are left out.
        var topicCounts = new Dictionary<string, int>();
        foreach (var response in responses)
            if (!string.IsNullOrEmpty(response.Topic))
                topicCounts[response.Topic] = topicCounts.GetValueOrDefault(response.Topic) + 1;

        // The three-way sentiment split, same thresholds as the dashboard.
        int positive = 0, neutral = 0, negative = 0;
        foreach (var response in responses)
            if (response.IsNegative)
                negative++;
            else if (response.SentimentScore is { } score && score >= 0.2)
                positive++;
            else if (response.SentimentScore is not null)
                neutral++;

        return new MonthlyReportData(
            project.Name,
            companyName,
            year,
            month,
            responses.Count,
            analysed,
            responses.Count(r => r.IsNegative),
            scores.Count == 0 ? "—" : scores.Average().ToString("+0.00;-0.00;0.00"),
            topicCounts.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).ToList(),
            new[] { ("ポジティブ", positive), ("中立", neutral), ("ネガティブ", negative) });
    }
}
