using System;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The analysis tables carry a 感情極性 column: time / region / weekday average the row sentiment
// (fact_response), while トピック別 averages each topic's own sentiment (fact_response_topic). Every
// report also drills to its 個票, which now carry the response's topic + sentiment.
public class AnalyticsSentimentTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    // Three 東京都 responses: row sentiment (drives time/region) and the per-topic sentiment (drives
    // トピック別) are made different on purpose. Topics: 満足 for the first two, 不満 for the third.
    private static (TempDatabase temp, AnalyticsRepository analytics, long pid) Seed()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.Address });
        var opinion = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(opinion);
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t", new[]
        {
            Response(("記入日", "2026/05/11"), ("都道府県", "東京都"), ("ご意見", "とても良い")),
            Response(("記入日", "2026/05/12"), ("都道府県", "東京都"), ("ご意見", "良い")),
            Response(("記入日", "2026/05/13"), ("都道府県", "東京都"), ("ご意見", "不満")),
        });

        topics.ReplaceTopics(opinion.Id, new (string, float[]?)[] { ("満足", null), ("不満", null) });
        var list = topics.ListTopics(opinion.Id);
        var satisfied = list.First(t => t.Label == "満足").Id;
        var dissatisfied = list.First(t => t.Label == "不満").Id;

        var ids = responses.LoadForProjectWithIds(pid);   // oldest first
        results.SaveRowSentiment(ids[0].Id, 0.8, false); results.SaveTopicAssignment(ids[0].Id, opinion.Id, satisfied, 0.9, false);
        results.SaveRowSentiment(ids[1].Id, 0.6, false); results.SaveTopicAssignment(ids[1].Id, opinion.Id, satisfied, 0.7, false);
        results.SaveRowSentiment(ids[2].Id, -0.4, true); results.SaveTopicAssignment(ids[2].Id, opinion.Id, dissatisfied, -0.5, true);
        analytics.Rebuild(project);

        return (temp, analytics, pid);
    }

    [Fact]
    public void Time_and_region_show_row_sentiment_topic_shows_per_topic_sentiment()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;
        var noColumns = Array.Empty<AnalysisColumn>();

        // Time / region: the 感情極性 column averages the ROW sentiment = (0.8 + 0.6 - 0.4) / 3 = +0.33.
        var year = analytics.AggregateRows(pid, AnalysisGrouping.Time, TimeScope.Root, null, null, noColumns).Rows.Single();
        Assert.Equal("+0.33", year.Sentiment);
        var tokyo = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, noColumns).Rows.Single();
        Assert.Equal("+0.33", tokyo.Sentiment);

        // Topic: each topic's 感情極性 is its OWN (per-column) sentiment — 満足 = (0.9 + 0.7)/2 = +0.80,
        // 不満 = -0.50 — not the row sentiment.
        var topicRows = analytics.AggregateRows(pid, AnalysisGrouping.Topic, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal("+0.80", topicRows.Single(r => r.Label == "満足").Sentiment);
        Assert.Equal("-0.50", topicRows.Single(r => r.Label == "不満").Sentiment);

        // The 全体 total also carries the average sentiment.
        var total = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, noColumns).Total;
        Assert.Equal("+0.33", total.Sentiment);
    }

    [Fact]
    public void Topic_report_can_be_scoped_to_one_question()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        // Two 自由記述 questions, each with its own topic dictionary.
        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var opinion = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        var improve = new DataField { Name = "改善点", FieldType = FieldType.FreeText };
        project.Fields.Add(opinion);
        project.Fields.Add(improve);
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t", new[]
        {
            Response(("記入日", "2026/05/11"), ("ご意見", "良い"), ("改善点", "高い")),
            Response(("記入日", "2026/05/12"), ("ご意見", "とても良い"), ("改善点", "やや高い")),
        });

        topics.ReplaceTopics(opinion.Id, new (string, float[]?)[] { ("満足", null) });
        topics.ReplaceTopics(improve.Id, new (string, float[]?)[] { ("価格", null) });
        var satisfied = topics.ListTopics(opinion.Id).Single().Id;
        var price = topics.ListTopics(improve.Id).Single().Id;

        foreach (var (id, _) in responses.LoadForProjectWithIds(pid))
        {
            results.SaveRowSentiment(id, 0.5, false);
            results.SaveTopicAssignment(id, opinion.Id, satisfied, 0.8, false);
            results.SaveTopicAssignment(id, improve.Id, price, -0.3, true);
        }
        analytics.Rebuild(project);
        var noColumns = Array.Empty<AnalysisColumn>();

        // Scoped to ご意見: only its topic 満足, with its own (+0.80) sentiment.
        var byOpinion = analytics.AggregateRows(pid, AnalysisGrouping.Topic, TimeScope.Root, null, null, noColumns, topicFieldId: opinion.Id).Rows;
        Assert.Equal(new[] { "満足" }, byOpinion.Select(r => r.Label).ToArray());
        Assert.Equal("+0.80", byOpinion.Single().Sentiment);

        // Scoped to 改善点: only its topic 価格 (-0.30).
        var byImprove = analytics.AggregateRows(pid, AnalysisGrouping.Topic, TimeScope.Root, null, null, noColumns, topicFieldId: improve.Id).Rows;
        Assert.Equal(new[] { "価格" }, byImprove.Select(r => r.Label).ToArray());
        Assert.Equal("-0.30", byImprove.Single().Sentiment);

        // Unscoped spans both questions' topics.
        var all = analytics.AggregateRows(pid, AnalysisGrouping.Topic, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal(new[] { "価格", "満足" }, all.Select(r => r.Label).OrderBy(l => l).ToArray());

        // The scoped topic 個票 returns the responses assigned that question's topic.
        Assert.Equal(2, analytics.ResponsesWithAnalysisForTopic(pid, "満足", null, null, opinion.Id).Count);
    }

    [Fact]
    public void Region_and_topic_drilldown_return_responses_with_analysis()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // 地域別 → 個票: every 東京都 response, each carrying its sentiment + topic.
        var inTokyo = analytics.ResponsesWithAnalysisForRegion(pid, "東京都", null, null);
        Assert.Equal(3, inTokyo.Count);
        Assert.All(inTokyo, r => Assert.NotNull(r.SentimentScore));
        Assert.Contains(inTokyo, r => r.Topic == "満足");
        Assert.Contains(inTokyo, r => r.Topic == "不満");

        // トピック別 → 個票: only the responses assigned that topic.
        var satisfied = analytics.ResponsesWithAnalysisForTopic(pid, "満足", null, null);
        Assert.Equal(2, satisfied.Count);
        Assert.All(satisfied, r => Assert.Equal("満足", r.Topic));
    }
}
