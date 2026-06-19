using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// AnalyticsRepository.SentimentTrend averages row sentiment per month within the window, in chronological
// order, over responses that have a sentiment score — the data behind the 感情極性の推移 line.
public class SentimentTrendTests
{
    private static SurveyResponse Resp(string date, string opinion) =>
        new() { Answers = new[] { new FieldAnswer("記入日", date), new FieldAnswer("ご意見", opinion) } };

    [Fact]
    public void Averages_by_month_in_chronological_order()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        projects.Insert(project);

        // Two responses in 2026/05, one in 2026/06.
        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/05/10", "a"), Resp("2026/05/20", "b"), Resp("2026/06/05", "c") });
        var rows = responses.LoadForProjectWithIds(project.Id);   // oldest first → a, b, c
        results.SaveRowSentiment(rows[0].Id, 0.4, false);
        results.SaveRowSentiment(rows[1].Id, 0.6, false);
        results.SaveRowSentiment(rows[2].Id, -0.2, true);
        analytics.Rebuild(project);

        var trend = analytics.SentimentTrend(project.Id, null, null);

        Assert.Equal(2, trend.Count);
        Assert.Equal((2026, 5), (trend[0].Year, trend[0].Month));
        Assert.Equal(0.5, trend[0].Average, 3);   // (0.4 + 0.6) / 2
        Assert.Equal(2, trend[0].Count);
        Assert.Equal((2026, 6), (trend[1].Year, trend[1].Month));
        Assert.Equal(-0.2, trend[1].Average, 3);
        Assert.Equal(1, trend[1].Count);
    }

    [Fact]
    public void Window_filters_months_and_unanalysed_rows_are_excluded()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/05/10", "a"), Resp("2026/06/05", "b") });
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.4, false);   // May analysed; June left unanalysed
        analytics.Rebuild(project);

        // Whole range: only the analysed (May) month appears.
        var all = analytics.SentimentTrend(project.Id, null, null);
        Assert.Single(all);
        Assert.Equal(5, all[0].Month);

        // Window restricted to June: no analysed rows → empty.
        Assert.Empty(analytics.SentimentTrend(project.Id, 20260601, 20260630));
    }
}
