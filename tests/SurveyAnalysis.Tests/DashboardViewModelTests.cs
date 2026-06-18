using System;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;
using Xunit;

namespace SurveyAnalysis.Tests;

public class DashboardViewModelTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers)
    {
        var list = new System.Collections.Generic.List<FieldAnswer>();
        foreach (var (field, value) in answers)
            list.Add(new FieldAnswer(field, value));
        return new SurveyResponse { Answers = list };
    }

    [Fact]
    public void Real_project_aggregates_imported_responses_for_the_selected_range()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "工事アンケート" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("氏名", "田中太郎"), ("ご意見", "料金プランの資料がほしい。")),
            Response(("記入日", "2026/05/21"), ("氏名", "佐藤花子"), ("ご意見", "助かった。")),
            Response(("記入日", "2026/04/10"), ("氏名", "別月さん"), ("ご意見", "先月分。")),
        });

        var vm = new DashboardViewModel(projects.Load(pid)!, analytics);
        // Pick an explicit May range so the test does not depend on today's date.
        vm.SetRange(DateRangePreset.Custom, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        // Only the two May responses are counted; April is filtered out.
        Assert.Equal(2, vm.TotalResponses);
        Assert.False(vm.HasNoResponses);

        // Topic/sentiment analytics await LLM.
        Assert.True(vm.AnalysisPending);
        Assert.Equal("—", vm.NegativeDisplay);
        Assert.Equal("—", vm.AverageSentiment);

        // Rows: date + free-text excerpt, newest first; topic/sentiment pending; never PII (names).
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("2026/05/21", vm.Rows[0].EntryDate);
        Assert.Equal("助かった。", vm.Rows[0].Excerpt);
        Assert.Equal("—", vm.Rows[0].Topic);
        Assert.Equal("—", vm.Rows[0].Sentiment);
        Assert.DoesNotContain(vm.Rows, r => r.Excerpt.Contains("田中") || r.Excerpt.Contains("佐藤"));
    }

    [Fact]
    public void Real_project_with_no_responses_shows_the_empty_state()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var pid = projects.Insert(project);

        var vm = new DashboardViewModel(projects.Load(pid)!, analytics);
        vm.SetRange(DateRangePreset.Custom, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        Assert.Equal(0, vm.TotalResponses);
        Assert.True(vm.HasNoResponses);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Real_project_shows_real_sentiment_and_topics_once_analysed()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "工事アンケート" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var freeText = new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment };
        project.Fields.Add(freeText);
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("ご意見", "丁寧で良かった。")),
            Response(("記入日", "2026/05/21"), ("ご意見", "配線が雑だった。")),
            Response(("記入日", "2026/05/22"), ("ご意見", "普通でした。")),
        });

        // Persisted analysis: two topics, a row sentiment + topic assignment per response.
        var 対応 = topics.AddTopic(freeText.Id, "対応");
        var 工事 = topics.AddTopic(freeText.Id, "工事");
        var ids = ResponseIds(temp);
        results.SaveRowSentiment(ids[0], 0.8, isNegative: false);
        results.SaveTopicAssignment(ids[0], freeText.Id, 対応, 0.8, isNegative: false);
        results.SaveRowSentiment(ids[1], -0.6, isNegative: true);
        results.SaveTopicAssignment(ids[1], freeText.Id, 工事, -0.6, isNegative: true);
        results.SaveRowSentiment(ids[2], 0.1, isNegative: false);
        results.SaveTopicAssignment(ids[2], freeText.Id, 対応, 0.1, isNegative: false);

        var vm = new DashboardViewModel(projects.Load(pid)!, analytics);
        vm.SetRange(DateRangePreset.Custom, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        // Analysis is present, so the real KPIs/charts show.
        Assert.False(vm.AnalysisPending);
        Assert.Equal(3, vm.TotalResponses);
        Assert.Equal("1", vm.NegativeDisplay);                 // one is_negative response
        Assert.Equal("+0.10", vm.AverageSentiment);            // (0.8 - 0.6 + 0.1) / 3

        // Topic bars: 対応 ×2 (largest first), 工事 ×1.
        Assert.Equal(2, vm.TopicBars.Count);
        Assert.Equal("対応", vm.TopicBars[0].Label);
        Assert.Equal(2, vm.TopicBars[0].Count);
        Assert.Equal("工事", vm.TopicBars[1].Label);

        // Sentiment distribution: ポジティブ 1 (0.8), 中立 1 (0.1), ネガティブ 1 (flagged).
        Assert.Equal(new[] { "ポジティブ", "中立", "ネガティブ" }, vm.SentimentBars.Select(b => b.Label).ToArray());
        Assert.Equal(new[] { 1, 1, 1 }, vm.SentimentBars.Select(b => b.Count).ToArray());

        // Rows (newest id first) carry the real topic + sentiment score.
        Assert.Equal("普通でした。", vm.Rows[0].Excerpt);
        Assert.Equal("対応", vm.Rows[0].Topic);
        Assert.Equal("+0.10", vm.Rows[0].Sentiment);
        Assert.Equal("-0.60", vm.Rows[1].Sentiment);           // the negative response
    }

    // The response ids for the single project under test, in insert order.
    private static long[] ResponseIds(TempDatabase temp)
    {
        using var connection = temp.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM responses ORDER BY id;";
        var ids = new System.Collections.Generic.List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }
}
