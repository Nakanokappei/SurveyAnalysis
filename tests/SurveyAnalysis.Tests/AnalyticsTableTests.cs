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
    [InlineData(FieldType.ChoiceNumber, AnalysisMethod.None, FieldAggregation.Average)]
    [InlineData(FieldType.PrefectureOnly, AnalysisMethod.None, FieldAggregation.DistinctCount)]
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
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.PrefectureOnly });
        project.Fields.Add(new DataField { Name = "評価", FieldType = FieldType.Number });
        project.Fields.Add(new DataField { Name = "満足度", FieldType = FieldType.ChoiceNumber });
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
        // 満足度=平均((4+2+5)/3=3.7), ご意見=感情平均(no LLM score yet → —).
        Assert.Equal(new[] { "3", "2", "10", "3.7", "—" }, year.Cells);

        // 全体 row: 平均 columns show the overall average; every other column shows the total 件数 (3).
        Assert.Equal("全体", table.Total.Label);
        Assert.Equal(3, table.Total.Count);
        Assert.Equal(new[] { "3", "3", "3", "3.7", "—" }, table.Total.Cells);
    }

    [Fact]
    public void Region_grouping_aggregates_each_prefecture()
    {
        var (temp, analytics, pid, columns) = Seed();
        using var _ = temp;

        var table = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, columns);
        var tokyo = table.Rows.Single(r => r.Label == "東京都");

        // 東京都 has two responses (05/11 評価3, 05/18 評価2): 都道府県種類数=1, 評価合計=5, 満足度平均=(4+5)/2=4.5.
        Assert.Equal(2, tokyo.Count);
        Assert.Equal("1", tokyo.Cells[1]); // 都道府県 distinct
        Assert.Equal("5", tokyo.Cells[2]); // 評価 sum
        Assert.Equal("4.5", tokyo.Cells[3]); // 満足度 average
    }
}
