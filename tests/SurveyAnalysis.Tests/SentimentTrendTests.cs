using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// AnalyticsRepository.SentimentTrend averages row sentiment over the window in chronological order, over
// responses that have a sentiment score — the data behind the 感情極性の推移 line. The bucket adapts to the
// data span: ≤ 30 days → one point per day; longer → one point per ISO week.
public class SentimentTrendTests
{
    private static SurveyResponse Resp(string date, string opinion) =>
        new() { Answers = new[] { new FieldAnswer("記入日", date), new FieldAnswer("ご意見", opinion) } };

    private static (TempDatabase temp, ProjectRepository projects, ResponseRepository responses,
        AnalysisResultsRepository results, AnalyticsRepository analytics, Project project) Setup()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);
        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        projects.Insert(project);
        return (temp, projects, responses, results, analytics, project);
    }

    [Fact]
    public void Daily_buckets_when_the_span_is_30_days_or_less()
    {
        var (temp, _, responses, results, analytics, project) = Setup();
        using var _t = temp;

        // Span 05/21..06/05 = 15 days ≤ 30 → one point per day, same-day responses averaged.
        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/05/21", "a"), Resp("2026/05/21", "b"), Resp("2026/06/05", "c") });
        var rows = responses.LoadForProjectWithIds(project.Id);   // oldest first → a, b, c
        results.SaveRowSentiment(rows[0].Id, 0.4, false);
        results.SaveRowSentiment(rows[1].Id, 0.6, false);
        results.SaveRowSentiment(rows[2].Id, -0.2, true);
        analytics.Rebuild(project);

        var trend = analytics.SentimentTrend(project.Id, null, null);

        Assert.Equal(2, trend.Count);
        Assert.Equal("5/21", trend[0].AxisLabel);
        Assert.Equal(0.5, trend[0].Average, 3);   // (0.4 + 0.6) / 2
        Assert.Equal(2, trend[0].Count);
        Assert.Equal("6/5", trend[1].AxisLabel);
        Assert.Equal(-0.2, trend[1].Average, 3);
        Assert.Equal(1, trend[1].Count);
    }

    [Fact]
    public void Weekly_buckets_when_the_span_exceeds_30_days()
    {
        var (temp, _, responses, results, analytics, project) = Setup();
        using var _t = temp;

        // Span 05/04..06/15 = 42 days > 30 → ISO-week buckets. 05/04 (Mon) and 05/05 (Tue) share a week.
        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/05/04", "a"), Resp("2026/05/05", "b"), Resp("2026/06/15", "c") });
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.2, false);
        results.SaveRowSentiment(rows[1].Id, 0.4, false);
        results.SaveRowSentiment(rows[2].Id, -0.3, true);
        analytics.Rebuild(project);

        var trend = analytics.SentimentTrend(project.Id, null, null);

        Assert.Equal(2, trend.Count);
        Assert.Equal(0.3, trend[0].Average, 3);   // week of 05/04: (0.2 + 0.4) / 2
        Assert.Equal(2, trend[0].Count);
        Assert.Equal(-0.3, trend[1].Average, 3);  // week of 06/15
        Assert.Equal(1, trend[1].Count);
        Assert.Contains("週", trend[0].Label);     // week label, e.g. "2026年 第19週"
    }

    [Fact]
    public void Window_filters_buckets_and_unanalysed_rows_are_excluded()
    {
        var (temp, _, responses, results, analytics, project) = Setup();
        using var _t = temp;

        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/05/21", "a"), Resp("2026/06/05", "b") });
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.4, false);   // May analysed; June left unanalysed
        analytics.Rebuild(project);

        // Whole range: only the analysed (May) day appears.
        var all = analytics.SentimentTrend(project.Id, null, null);
        Assert.Single(all);
        Assert.Equal("5/21", all[0].AxisLabel);

        // Window restricted to June: no analysed rows → empty.
        Assert.Empty(analytics.SentimentTrend(project.Id, 20260601, 20260630));
    }
}
