using System;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// AnalyticsRepository.SentimentTrend averages row sentiment over the window in chronological order, over
// responses that have a sentiment score — the data behind the 感情極性の推移 line. The bucket adapts to the
// data span: ≤ 30 days → one point per day; 31–183 days → one point per ISO week; > 183 days (over half a
// year) → one point per calendar month.
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

        // Each daily point's range is that single day (clicking it narrows the 集計期間 to the day).
        Assert.Equal(new DateTime(2026, 5, 21), trend[0].From);
        Assert.Equal(new DateTime(2026, 5, 21), trend[0].To);
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

        // The week point's range is the whole ISO week (Mon–Sun): 2026/05/04 (Mon) … 05/10 (Sun).
        Assert.Equal(new DateTime(2026, 5, 4), trend[0].From);
        Assert.Equal(new DateTime(2026, 5, 10), trend[0].To);
    }

    [Fact]
    public void Monthly_buckets_when_the_span_exceeds_half_a_year()
    {
        var (temp, _, responses, results, analytics, project) = Setup();
        using var _t = temp;

        // Span 01/10..08/20 = 222 days > 183 → calendar-month buckets. The two January days share a month.
        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/01/10", "a"), Resp("2026/01/25", "b"), Resp("2026/08/20", "c") });
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.2, false);
        results.SaveRowSentiment(rows[1].Id, 0.4, false);
        results.SaveRowSentiment(rows[2].Id, -0.6, true);
        analytics.Rebuild(project);

        var trend = analytics.SentimentTrend(project.Id, null, null);

        Assert.Equal(2, trend.Count);
        Assert.Equal("2026/1", trend[0].AxisLabel);
        Assert.Equal(0.3, trend[0].Average, 3);   // January: (0.2 + 0.4) / 2
        Assert.Equal(2, trend[0].Count);
        Assert.Equal("2026年1月", trend[0].Label);   // month label (tooltip)
        Assert.Equal("2026/8", trend[1].AxisLabel);
        Assert.Equal(-0.6, trend[1].Average, 3);

        // The month point's range is the whole calendar month: 2026/01/01 … 01/31 (clicking narrows to it).
        Assert.Equal(new DateTime(2026, 1, 1), trend[0].From);
        Assert.Equal(new DateTime(2026, 1, 31), trend[0].To);
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

    [Fact]
    public void Scoped_to_a_region_filters_the_trend()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.Address });
        projects.Insert(project);

        SurveyResponse R(string date, string pref) =>
            new() { Answers = new[] { new FieldAnswer("記入日", date), new FieldAnswer("都道府県", pref) } };
        responses.InsertResponses(project.Id, "t", new[] { R("2026/06/01", "東京都"), R("2026/06/02", "神奈川県"), R("2026/06/03", "東京都") });
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.8, false);
        results.SaveRowSentiment(rows[1].Id, -0.4, true);
        results.SaveRowSentiment(rows[2].Id, 0.2, false);
        analytics.Rebuild(project);

        // 東京都 only: 06/01 (+0.8) and 06/03 (+0.2).
        var tokyo = analytics.SentimentTrendForRegion(project.Id, "東京都", null, null);
        Assert.Equal(new[] { "6/1", "6/3" }, tokyo.Select(p => p.AxisLabel).ToArray());
        Assert.Equal(0.8, tokyo[0].Average, 3);
        Assert.Equal(0.2, tokyo[1].Average, 3);

        // 神奈川県 only: a single 06/02 point (−0.4).
        var kanagawa = analytics.SentimentTrendForRegion(project.Id, "神奈川県", null, null);
        Assert.Single(kanagawa);
        Assert.Equal(-0.4, kanagawa[0].Average, 3);
    }

    [Fact]
    public void Scoped_to_a_topic_filters_the_trend()
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

        responses.InsertResponses(project.Id, "t", new[] { Resp("2026/06/01", "a"), Resp("2026/06/02", "b"), Resp("2026/06/03", "c") });
        topics.ReplaceTopics(opinion.Id, new (string, float[]?)[] { ("満足", null), ("不満", null) });
        var list = topics.ListTopics(opinion.Id);
        var satisfied = list.First(t => t.Label == "満足").Id;
        var dissatisfied = list.First(t => t.Label == "不満").Id;
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.8, false); results.SaveTopicAssignment(rows[0].Id, opinion.Id, satisfied, 0.8, false);
        results.SaveRowSentiment(rows[1].Id, -0.4, true); results.SaveTopicAssignment(rows[1].Id, opinion.Id, dissatisfied, -0.4, true);
        results.SaveRowSentiment(rows[2].Id, 0.2, false); results.SaveTopicAssignment(rows[2].Id, opinion.Id, satisfied, 0.2, false);
        analytics.Rebuild(project);

        // 満足-assigned responses' row sentiment over time: 06/01 (+0.8), 06/03 (+0.2).
        var satisfiedTrend = analytics.SentimentTrendForTopic(project.Id, "満足", null, null);
        Assert.Equal(new[] { "6/1", "6/3" }, satisfiedTrend.Select(p => p.AxisLabel).ToArray());
        Assert.Equal(0.8, satisfiedTrend[0].Average, 3);
        Assert.Equal(0.2, satisfiedTrend[1].Average, 3);

        // 不満: only 06/02 (−0.4).
        var dissatisfiedTrend = analytics.SentimentTrendForTopic(project.Id, "不満", null, null);
        Assert.Single(dissatisfiedTrend);
        Assert.Equal(-0.4, dissatisfiedTrend[0].Average, 3);
    }
}
