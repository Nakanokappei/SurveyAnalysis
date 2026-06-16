using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The enriched star: 選択肢 fields become a dim_choice dimension reached through a bridge (so a
// response can answer several choice fields independently), and the region dimension groups by the
// parsed 都道府県, collapsing distinct full addresses in the same prefecture.
public class StarSchemaTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    private static IReadOnlyList<AnalysisColumn> Columns(Project project) =>
        project.Fields.Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f))).ToList();

    [Fact]
    public void Choice_grouping_buckets_by_one_choice_field_without_leaking_other_choice_fields()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "満足度", FieldType = FieldType.Choice });
        project.Fields.Add(new DataField { Name = "工事種別", FieldType = FieldType.Choice });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);
        var satisfactionId = project.Fields[1].Id;
        var workTypeId = project.Fields[2].Id;

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("満足度", "満足"), ("工事種別", "新設"), ("ご意見", "良い")),
            Response(("記入日", "2026/05/12"), ("満足度", "不満"), ("工事種別", "修理"), ("ご意見", "うーん")),
            Response(("記入日", "2026/05/13"), ("満足度", "満足"), ("工事種別", "新設"), ("ご意見", "最高")),
            // No 満足度, but a 工事種別: must land in （未選択） for 満足度, not leak via the other field.
            Response(("記入日", "2026/05/14"), ("工事種別", "撤去"), ("ご意見", "普通")),
        });
        analytics.Rebuild(project);

        var bySatisfaction = analytics.AggregateRows(
            pid, AnalysisGrouping.Choice, TimeScope.Root, null, null, Columns(project), satisfactionId);
        Assert.Equal(4, bySatisfaction.Rows.Sum(r => r.Count));
        Assert.Equal("満足", bySatisfaction.Rows[0].Label);  // largest first (2)
        Assert.Equal(2, bySatisfaction.Rows[0].Count);
        Assert.Equal(1, bySatisfaction.Rows.Single(r => r.Label == "不満").Count);
        Assert.Equal(1, bySatisfaction.Rows.Single(r => r.Label == "（未選択）").Count);

        // The other choice field groups independently: every response has a 工事種別, so no （未選択）.
        var byWorkType = analytics.AggregateRows(
            pid, AnalysisGrouping.Choice, TimeScope.Root, null, null, Columns(project), workTypeId);
        Assert.Equal(4, byWorkType.Rows.Sum(r => r.Count));
        Assert.DoesNotContain(byWorkType.Rows, r => r.Label == "（未選択）");
        Assert.Equal(2, byWorkType.Rows.Single(r => r.Label == "新設").Count);
    }

    [Fact]
    public void Multi_select_choice_splits_into_separate_options()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "きっかけ", FieldType = FieldType.Choice });
        var pid = projects.Insert(project);
        var kikkakeId = project.Fields[1].Id;

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("きっかけ", "テレビ; ネット; 電話")),
            Response(("記入日", "2026/05/12"), ("きっかけ", "ネット")),
            Response(("記入日", "2026/05/13"), ("きっかけ", "テレビ; ネット")),
        });
        analytics.Rebuild(project);

        var table = analytics.AggregateRows(
            pid, AnalysisGrouping.Choice, TimeScope.Root, null, null, Columns(project), kikkakeId);

        // A multi-select response lands in every option it chose, so counts sum past the 3 responses.
        Assert.Equal(3, table.Rows.Single(r => r.Label == "ネット").Count);   // all three
        Assert.Equal(2, table.Rows.Single(r => r.Label == "テレビ").Count);   // r1, r3
        Assert.Equal(1, table.Rows.Single(r => r.Label == "電話").Count);     // r1 only
        Assert.Equal(3, table.Rows.Count);                                     // three distinct options

        // dim_choice holds one row per distinct option for the field (not per whole cell).
        using var connection = temp.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dim_choice WHERE field_id = $f;";
        command.Parameters.AddWithValue("$f", kikkakeId);
        Assert.Equal(3L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Region_grouping_collapses_full_addresses_to_prefecture()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "住所", FieldType = FieldType.Address });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("住所", "東京都新宿区西新宿1-1")),
            Response(("記入日", "2026/05/12"), ("住所", "東京都渋谷区道玄坂2-2")),
            Response(("記入日", "2026/05/13"), ("住所", "大阪府大阪市北区梅田3-3")),
        });
        analytics.Rebuild(project);

        // Two distinct Tokyo addresses collapse into one 東京都 group of 2.
        var table = analytics.AggregateRows(pid, AnalysisGrouping.Region, TimeScope.Root, null, null, Columns(project));
        Assert.Equal("東京都", table.Rows[0].Label);
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Equal(1, table.Rows.Single(r => r.Label == "大阪府").Count);

        // The ETL stored the parsed 市区町村 alongside each full address.
        using var connection = temp.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT city FROM dim_region WHERE label = '東京都新宿区西新宿1-1';";
        Assert.Equal("新宿区", (string)command.ExecuteScalar()!);
    }
}
