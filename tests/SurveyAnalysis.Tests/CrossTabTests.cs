using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// AggregateCrossTab pivots a 軸 (時間別 / 曜日別 / 地域別) scope against a 質問's categories: one row per axis
// group, one count cell per category (topics of a 自由記述 question, or options of a 選択肢 question), plus a
// 感情極性 column and a trailing 合計. A multi-select option counts under each chosen value, so category
// cells can sum past 合計. CrossTabCategories fixes the column order (dictionary + unset).
public class CrossTabTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    [Fact]
    public void Region_by_topic_counts_each_groups_responses_per_topic()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "住所", FieldType = FieldType.Address });
        var opinion = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(opinion);
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Response(("記入日", "2026/05/01"), ("住所", "東京都新宿区1-1"), ("ご意見", "a")),
            Response(("記入日", "2026/05/02"), ("住所", "東京都渋谷区2-2"), ("ご意見", "b")),
            Response(("記入日", "2026/05/03"), ("住所", "神奈川県横浜市3-3"), ("ご意見", "c")),
            Response(("記入日", "2026/05/04"), ("住所", "東京都港区4-4"), ("ご意見", "d")),   // 未分析
        });
        topics.ReplaceTopics(opinion.Id, new (string, float[]?)[] { ("満足", null), ("不満", null) });
        var list = topics.ListTopics(opinion.Id);
        var satisfied = list.First(t => t.Label == "満足").Id;
        var dissatisfied = list.First(t => t.Label == "不満").Id;
        var rows = responses.LoadForProjectWithIds(project.Id);
        results.SaveRowSentiment(rows[0].Id, 0.8, false); results.SaveTopicAssignment(rows[0].Id, opinion.Id, satisfied, 0.8, false);
        results.SaveRowSentiment(rows[1].Id, -0.4, true); results.SaveTopicAssignment(rows[1].Id, opinion.Id, dissatisfied, -0.4, true);
        results.SaveRowSentiment(rows[2].Id, 0.2, false); results.SaveTopicAssignment(rows[2].Id, opinion.Id, satisfied, 0.2, false);
        results.SaveRowSentiment(rows[3].Id, 0.1, false);   // no topic assignment → （未分析）
        analytics.Rebuild(project);

        var spec = new CrossTabSpec(CrossTabKind.Topic, opinion.Id, "ご意見");
        var categories = analytics.CrossTabCategories(spec);
        Assert.Equal(new[] { "満足", "不満", "（未分析）" }, categories.ToArray());

        var table = analytics.AggregateCrossTab(project.Id, AnalysisGrouping.Region, TimeScope.Root, null, null, spec, categories);

        // Rows largest-first: 東京都 (3) then 神奈川県 (1).
        Assert.Equal("東京都", table.Rows[0].Label);
        Assert.Equal(new[] { "1", "1", "1", "3" }, table.Rows[0].Cells.ToArray());   // 満足 / 不満 / 未分析 / 合計
        Assert.Equal("+0.17", table.Rows[0].Sentiment);                              // (0.8 - 0.4 + 0.1) / 3
        Assert.Equal(3, table.Rows[0].Count);

        Assert.Equal("神奈川県", table.Rows[1].Label);
        Assert.Equal(new[] { "1", "0", "0", "1" }, table.Rows[1].Cells.ToArray());

        // 全体 row holds the column totals.
        Assert.Equal(new[] { "2", "1", "1", "4" }, table.Total.Cells.ToArray());
        Assert.Equal(4, table.Total.Count);
    }

    [Fact]
    public void Time_by_multi_select_choice_counts_under_each_chosen_option()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var trigger = new DataField { Name = "きっかけ", FieldType = FieldType.Choice };
        project.Fields.Add(trigger);
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Response(("記入日", "2026/05/11"), ("きっかけ", "テレビ; ネット")),
            Response(("記入日", "2026/05/12"), ("きっかけ", "ネット")),
            Response(("記入日", "2026/05/13"), ("きっかけ", "テレビ")),
        });
        analytics.Rebuild(project);

        var spec = new CrossTabSpec(CrossTabKind.Choice, trigger.Id, "きっかけ");
        var categories = analytics.CrossTabCategories(spec);
        Assert.Equal(new[] { "テレビ", "ネット", "（未選択）" }, categories.ToArray());

        // All three responses fall in one 年度 group; columns split the multi-select cell.
        var table = analytics.AggregateCrossTab(project.Id, AnalysisGrouping.Time, TimeScope.Root, null, null, spec, categories);
        var row = Assert.Single(table.Rows);
        Assert.Equal(new[] { "2", "2", "0", "3" }, row.Cells.ToArray());   // テレビ(r1,r3) / ネット(r1,r2) / 未選択 / 合計(distinct)
        Assert.Equal(3, row.Count);                                        // 2 + 2 sums past the 3 responses
        Assert.Equal("—", row.Sentiment);                                  // no sentiment scored
    }

    [Fact]
    public void Choice_individual_responses_match_each_option()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var trigger = new DataField { Name = "きっかけ", FieldType = FieldType.Choice };
        project.Fields.Add(trigger);
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Response(("記入日", "2026/05/11"), ("きっかけ", "テレビ; ネット")),
            Response(("記入日", "2026/05/12"), ("きっかけ", "ネット")),
            Response(("記入日", "2026/05/13")),   // no 選択肢 → （未選択）
        });
        analytics.Rebuild(project);

        Assert.Equal(2, analytics.ResponsesWithAnalysisForChoice(project.Id, trigger.Id, "ネット", null, null).Count);
        Assert.Single(analytics.ResponsesWithAnalysisForChoice(project.Id, trigger.Id, "テレビ", null, null));
        Assert.Single(analytics.ResponsesWithAnalysisForChoice(project.Id, trigger.Id, "（未選択）", null, null));
    }
}
