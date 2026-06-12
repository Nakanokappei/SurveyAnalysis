using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

public class AnalyticsRepositoryTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers)
    {
        var list = new List<FieldAnswer>();
        foreach (var (field, value) in answers)
            list.Add(new FieldAnswer(field, value));
        return new SurveyResponse { Answers = list };
    }

    [Fact]
    public void Rebuild_then_time_slice_counts_responses_by_month_newest_first()
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
            Response(("記入日", "2026/05/20"), ("ご意見", "a")),
            Response(("記入日", "2026/05/21"), ("ご意見", "b")),
            Response(("記入日", "2026/04/10"), ("ご意見", "c")),
        });

        analytics.Rebuild(projects.Load(pid)!);
        var byTime = analytics.AggregateByTime(pid, TimeGrain.Month);

        Assert.Equal(2, byTime.Count);
        Assert.Equal(("2026年5月", 2), byTime[0]); // newest month first
        Assert.Equal(("2026年4月", 1), byTime[1]);
    }

    [Fact]
    public void Time_slice_supports_fiscal_year_quarter_and_day_of_week()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20")), // 水曜日, 2026年度 Q1 (Apr–Jun)
            Response(("記入日", "2026/05/21")), // 木曜日, 2026年度 Q1
            Response(("記入日", "2026/03/10")), // 火曜日, Jan–Mar → 前年度 2025年度 Q4
        });
        analytics.Rebuild(projects.Load(pid)!);

        // 年度別 (April-start): May → 2026年度, March → previous 2025年度.
        Assert.Equal(new[] { ("2026年度", 2), ("2025年度", 1) },
            analytics.AggregateByTime(pid, TimeGrain.FiscalYear).ToArray());

        // 四半期別: Apr–Jun = Q1, Jan–Mar = Q4 of the previous fiscal year.
        Assert.Equal(new[] { ("2026年度 Q1", 2), ("2025年度 Q4", 1) },
            analytics.AggregateByTime(pid, TimeGrain.FiscalQuarter).ToArray());

        // 曜日別: ordered Mon→Sun.
        Assert.Equal(new[] { ("火曜日", 1), ("水曜日", 1), ("木曜日", 1) },
            analytics.AggregateByTime(pid, TimeGrain.DayOfWeek).ToArray());
    }

    [Fact]
    public void Region_slice_groups_by_prefecture_field()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.PrefectureOnly });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("都道府県", "東京都")),
            Response(("記入日", "2026/05/21"), ("都道府県", "東京都")),
            Response(("記入日", "2026/05/22"), ("都道府県", "神奈川県")),
        });

        analytics.Rebuild(projects.Load(pid)!);
        var byRegion = analytics.AggregateBy(pid, SliceKind.Region);

        Assert.Equal(("東京都", 2), byRegion[0]); // largest first
        Assert.Equal(("神奈川県", 1), byRegion[1]);
    }

    [Fact]
    public void Without_region_field_facts_fall_into_unset_bucket()
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
            Response(("記入日", "2026/05/20"), ("ご意見", "a")),
            Response(("記入日", "2026/05/21"), ("ご意見", "b")),
        });

        analytics.Rebuild(projects.Load(pid)!);

        var byRegion = analytics.AggregateBy(pid, SliceKind.Region);
        Assert.Equal(new[] { ("（未設定）", 2) }, byRegion);

        // No topic field / no LLM yet → everything is unanalyzed.
        var byTopic = analytics.AggregateBy(pid, SliceKind.Topic);
        Assert.Equal(new[] { ("（未分析）", 2) }, byTopic);
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

        var loaded = projects.Load(pid)!;
        analytics.Rebuild(loaded);
        analytics.Rebuild(loaded); // second run must not double-count

        Assert.Equal(new[] { ("2026年5月", 1) }, analytics.AggregateByTime(pid, TimeGrain.Month).ToArray());
    }
}
