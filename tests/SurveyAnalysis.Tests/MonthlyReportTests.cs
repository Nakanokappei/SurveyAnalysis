using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using SurveyAnalysis.Reports;
using Xunit;

namespace SurveyAnalysis.Tests;

// MonthlyReportBuilder computes a calendar month's KPI figures, topic breakdown and sentiment split from
// the analytics star — the same metrics (and thresholds) the dashboard shows, scoped to [1日, 末日].
public class MonthlyReportTests
{
    private static SurveyResponse Resp(string date, string opinion) =>
        new() { Answers = new[] { new FieldAnswer("記入日", date), new FieldAnswer("ご意見", opinion) } };

    [Fact]
    public void Builds_the_chosen_months_kpis_topics_and_sentiment_split()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var opinion = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(opinion);
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Resp("2026/05/10", "a"),   // +0.80 満足   → positive
            Resp("2026/05/20", "b"),   // -0.40 不満   → negative
            Resp("2026/05/25", "c"),   // +0.10 満足   → neutral (scored, < 0.2)
            Resp("2026/06/05", "d"),   // +0.50 満足   → different month, excluded
        });
        topics.ReplaceTopics(opinion.Id, new (string, float[]?)[] { ("満足", null), ("不満", null) });
        var list = topics.ListTopics(opinion.Id);
        var satisfied = list.First(t => t.Label == "満足").Id;
        var dissatisfied = list.First(t => t.Label == "不満").Id;
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.8, false); results.SaveTopicAssignment(rows[0].Id, opinion.Id, satisfied, 0.8, false);
        results.SaveRowSentiment(rows[1].Id, -0.4, true); results.SaveTopicAssignment(rows[1].Id, opinion.Id, dissatisfied, -0.4, true);
        results.SaveRowSentiment(rows[2].Id, 0.1, false); results.SaveTopicAssignment(rows[2].Id, opinion.Id, satisfied, 0.1, false);
        results.SaveRowSentiment(rows[3].Id, 0.5, false); results.SaveTopicAssignment(rows[3].Id, opinion.Id, satisfied, 0.5, false);
        analytics.Rebuild(project);

        var report = MonthlyReportBuilder.Build(analytics, project, "関東ケーブルテレビ", 2026, 5);

        Assert.Equal("P", report.ProjectName);
        Assert.Equal("関東ケーブルテレビ", report.CompanyName);
        Assert.Equal("2026年5月", report.MonthLabel);
        Assert.Equal(3, report.TotalResponses);            // June's response is out of the month window
        Assert.Equal(3, report.AnalysedResponses);
        Assert.Equal(1, report.NegativeCount);
        Assert.Equal("+0.17", report.AverageSentiment);    // (0.8 - 0.4 + 0.1) / 3

        Assert.Equal(("満足", 2), report.TopicCounts[0]);    // largest first
        Assert.Equal(("不満", 1), report.TopicCounts[1]);

        // ポジティブ / 中立 / ネガティブ split, in that fixed order.
        Assert.Equal(new[] { ("ポジティブ", 1), ("中立", 1), ("ネガティブ", 1) }, report.SentimentDistribution.ToArray());
    }
}
