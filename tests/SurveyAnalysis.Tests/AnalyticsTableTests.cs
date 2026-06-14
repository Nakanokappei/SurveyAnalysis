using System;
using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The analysis table (rows × all project fields). Verifies the per-field aggregation derived from the
// field type (種類数 / 合計 / 平均 / 感情平均) and that the engine fills cells aligned to the columns.
public class AnalyticsTableTests
{
    [Theory]
    [InlineData(FieldType.Number, AnalysisMethod.None, FieldAggregation.Sum)]
    [InlineData(FieldType.Choice, AnalysisMethod.None, FieldAggregation.DistinctCount)]
    [InlineData(FieldType.Text, AnalysisMethod.None, FieldAggregation.DistinctCount)]
    [InlineData(FieldType.Date, AnalysisMethod.None, FieldAggregation.DistinctCount)]
    [InlineData(FieldType.Name, AnalysisMethod.None, FieldAggregation.DistinctCount)]
    [InlineData(FieldType.FreeText, AnalysisMethod.Sentiment, FieldAggregation.SentimentAverage)]
    public void Aggregation_is_derived_from_field_type(FieldType type, AnalysisMethod analysis, FieldAggregation expected)
    {
        var field = new DataField { Name = "f", FieldType = type, Analysis = analysis };
        Assert.Equal(expected, FieldAggregationInfo.For(field));
    }

    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    private static (TempDatabase temp, AnalyticsRepository analytics, long pid, IReadOnlyList<AnalysisColumn> columns) Seed()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.Address });
        project.Fields.Add(new DataField { Name = "評価", FieldType = FieldType.Number });
        project.Fields.Add(new DataField { Name = "満足度", FieldType = FieldType.Choice });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("都道府県", "東京都"), ("評価", "3"), ("満足度", "4"), ("ご意見", "良い")),
            Response(("記入日", "2026/05/12"), ("都道府県", "大阪府"), ("評価", "5"), ("満足度", "2"), ("ご意見", "普通")),
            Response(("記入日", "2026/05/18"), ("都道府県", "東京都"), ("評価", "2"), ("満足度", "5"), ("ご意見", "良い")),
        });
        analytics.Rebuild(project);

        var columns = project.Fields.Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f))).ToList();
        return (temp, analytics, pid, columns);
    }

    [Fact]
    public void Rows_carry_one_aggregated_cell_per_field()
    {
        var (temp, analytics, pid, columns) = Seed();
        using var _ = temp;

        // 全期間 → grouped by 年度: all three May-2026 responses fall in 2026年度.
        var table = analytics.AggregateRows(pid, AnalysisGrouping.Time, TimeScope.Root, null, null, columns);
        var year = table.Rows.Single();

        Assert.Equal("2026年度", year.Label);
        Assert.Equal(3, year.Count);
        // 記入日=種類数(3 distinct dates), 都道府県=種類数(東京都/大阪府=2), 評価=合計(3+5+2=10),
        // 満足度=種類数(4/2/5=3 distinct), ご意見=感情平均(no LLM score yet → —).
        Assert.Equal(new[] { "3", "2", "10", "3", "—" }, year.Cells);

        // 全体 row: each column aggregated over all responses with its own method — 記入日種類数=3,
        // 都道府県種類数=2 (東京都/大阪府), 評価合計=10, 満足度種類数=3, ご意見感情平均=—. (One group here,
        // so the total equals that group's cells.)
        Assert.Equal("全体", table.Total.Label);
        Assert.Equal(3, table.Total.Count);
        Assert.Equal(new[] { "3", "2", "10", "3", "—" }, table.Total.Cells);
    }

    [Fact]
    public void Region_grouping_aggregates_each_prefecture()
    {
        var (temp, analytics, pid, columns) = Seed();
        using var _ = temp;

        var table = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, columns);
        Assert.Equal("東京都", table.Rows[0].Label); // largest group first
        var tokyo = table.Rows.Single(r => r.Label == "東京都");

        // 東京都 has two responses (05/11 評価3 満足度4, 05/18 評価2 満足度5): 都道府県種類数=1, 評価合計=5,
        // 満足度種類数={4,5}=2.
        Assert.Equal(2, tokyo.Count);
        Assert.Equal("1", tokyo.Cells[1]); // 都道府県 distinct
        Assert.Equal("5", tokyo.Cells[2]); // 評価 sum
        Assert.Equal("2", tokyo.Cells[3]); // 満足度 distinct
    }

    [Fact]
    public void Total_row_distinct_count_is_over_all_responses_not_the_response_count()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.Address });
        var pid = projects.Insert(project);
        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("都道府県", "東京都")),
            Response(("記入日", "2026/06/11"), ("都道府県", "東京都")), // same prefecture, different month
            Response(("記入日", "2026/07/11"), ("都道府県", "大阪府")),
        });
        analytics.Rebuild(project);

        var columns = new[] { new AnalysisColumn("都道府県", FieldAggregation.DistinctCount) };
        var year = analytics.AggregateRows(pid, AnalysisGrouping.Time, TimeScope.Root, null, null, columns).Rows.Single();
        var months = analytics.AggregateRows(pid, AnalysisGrouping.Time, year.ChildScope!, null, null, columns);

        // Three month groups, each with one distinct prefecture.
        Assert.Equal(3, months.Rows.Count);
        Assert.All(months.Rows, r => Assert.Equal("1", r.Cells[0]));
        // 全体 = distinct prefectures across all responses = {東京都, 大阪府} = 2 — not the 3 responses,
        // and not 1+1+1 summed across groups.
        Assert.Equal("2", months.Total.Cells[0]);
    }

    [Fact]
    public void Region_without_field_is_unset_and_topic_is_unanalyzed()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);
        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("ご意見", "a")),
            Response(("記入日", "2026/05/12"), ("ご意見", "b")),
        });
        analytics.Rebuild(project);
        var noColumns = Array.Empty<AnalysisColumn>();

        // No region field → every response falls into 「（未設定）」; no LLM topic → 「（未分析）」.
        var region = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal(new[] { ("（未設定）", 2) }, region.Select(r => (r.Label, r.Count)).ToArray());

        var topic = analytics.AggregateRows(pid, AnalysisGrouping.Topic, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal(new[] { ("（未分析）", 2) }, topic.Select(r => (r.Label, r.Count)).ToArray());
    }

    [Fact]
    public void Rebuild_is_idempotent()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var pid = projects.Insert(project);
        responses.InsertResponses(pid, "t.csv", new[] { Response(("記入日", "2026/05/20")) });

        analytics.Rebuild(project);
        analytics.Rebuild(project); // second run must not double-count

        var years = analytics.AggregateRows(pid, AnalysisGrouping.Time, TimeScope.Root, null, null, Array.Empty<AnalysisColumn>()).Rows;
        Assert.Equal(new[] { ("2026年度", 1) }, years.Select(r => (r.Label, r.Count)).ToArray());
    }
}
