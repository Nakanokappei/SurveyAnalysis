using System;
using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The time drill-down behind 時間別 → 期間, driven through the production AggregateRows path: 全期間→
// 年度→月→週→日, then the 個票 via ResponsesForScope/ResponsesForWeekday. The seed straddles two fiscal
// years (April-start) and puts two responses in one ISO week so the week/day split is observable.
// 2026/05/11 is a Monday, so 05/11 and 05/12 share an ISO week and 05/18 starts the next.
public class AnalyticsDrillDownTests
{
    private static readonly AnalysisColumn[] NoColumns = Array.Empty<AnalysisColumn>();

    private static SurveyResponse Response(string date, string comment) =>
        new()
        {
            Answers = new List<FieldAnswer> { new("記入日", date), new("ご意見", comment) }
        };

    private static (TempDatabase temp, AnalyticsRepository analytics, long pid) Seed()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response("2026/05/11", "月曜の朝"),   // FY2026, 2026年5月, ISO week A, Mon
            Response("2026/05/12", "火曜"),       // FY2026, 2026年5月, ISO week A, Tue
            Response("2026/05/18", "翌週"),       // FY2026, 2026年5月, ISO week B, Mon
            Response("2026/08/03", "夏"),         // FY2026, 2026年8月, Mon
            Response("2026/02/14", "冬"),         // FY2025 (Jan–Mar → 前年度), Sat
            Response("2025/12/25", "年末"),       // FY2025, Thu
        });

        analytics.Rebuild(project);
        return (temp, analytics, pid);
    }

    // The child rows of a time scope (year/month/week/day depending on depth), via the live path.
    private static IReadOnlyList<AnalysisRow> TimeRows(AnalyticsRepository a, long pid, TimeScope scope, long? from = null, long? to = null) =>
        a.AggregateRows(pid, AnalysisGrouping.Time, scope, from, to, NoColumns).Rows;

    private static AnalysisRow Row(IReadOnlyList<AnalysisRow> rows, string label) => rows.Single(r => r.Label == label);

    [Fact]
    public void Root_children_are_fiscal_years()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        var years = TimeRows(analytics, pid, TimeScope.Root);

        Assert.Equal(4, Row(years, "2026年度").Count);
        Assert.Equal(2, Row(years, "2025年度").Count);
        Assert.Equal(6, years.Sum(r => r.Count));            // parent total = sum of children
        Assert.Equal("2026年度", years[0].Label);             // newest fiscal year first
    }

    [Fact]
    public void Drill_year_to_month_then_month_to_week_then_week_to_day()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // 年度 → 月 (newest month first)
        var months = TimeRows(analytics, pid, Row(TimeRows(analytics, pid, TimeScope.Root), "2026年度").ChildScope!);
        Assert.Equal(new[] { "2026年8月", "2026年5月" }, months.Select(r => r.Label).ToArray());
        Assert.Equal(3, Row(months, "2026年5月").Count);

        // 月 → 週: 2026年5月 splits into two ISO weeks summing to 3 (one week has the 11th & 12th).
        var weeks = TimeRows(analytics, pid, Row(months, "2026年5月").ChildScope!);
        Assert.Equal(2, weeks.Count);
        Assert.Equal(3, weeks.Sum(w => w.Count));
        var weekOfTwo = weeks.Single(w => w.Count == 2);

        // 週 → 日: that week breaks into the two individual dates.
        var days = TimeRows(analytics, pid, weekOfTwo.ChildScope!);
        Assert.Equal(2, days.Count);
        Assert.Equal(1, Row(days, "2026-05-11").Count);
        Assert.Equal(1, Row(days, "2026-05-12").Count);
    }

    [Fact]
    public void Day_scope_lists_its_individual_responses()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        var months = TimeRows(analytics, pid, Row(TimeRows(analytics, pid, TimeScope.Root), "2026年度").ChildScope!);
        var weeks = TimeRows(analytics, pid, Row(months, "2026年5月").ChildScope!);
        var days = TimeRows(analytics, pid, weeks.Single(w => w.Count == 2).ChildScope!);
        var may11 = Row(days, "2026-05-11");

        Assert.True(may11.ChildScope!.IsTerminal); // a day is the deepest level → individual responses

        var rows = analytics.ResponsesForScope(pid, may11.ChildScope!);
        Assert.Single(rows);
        Assert.Equal("2026/05/11", rows[0]["記入日"]);
        Assert.Equal("月曜の朝", rows[0]["ご意見"]);
    }

    [Fact]
    public void Window_filters_drill_and_weekday_to_the_date_range()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // Window = May 2026 only → the three May responses (11th, 12th, 18th), all in 2026年度.
        var years = TimeRows(analytics, pid, TimeScope.Root, 20260501, 20260531);
        Assert.Equal(new[] { ("2026年度", 3) }, years.Select(r => (r.Label, r.Count)).ToArray());

        var weekdays = analytics.AggregateRows(pid, AnalysisGrouping.Weekday, TimeScope.Root, 20260501, 20260531, NoColumns).Rows;
        Assert.Equal(3, weekdays.Sum(r => r.Count));
    }

    [Fact]
    public void Weekday_grouping_orders_monday_first()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        var weekdays = analytics.AggregateRows(pid, AnalysisGrouping.Weekday, TimeScope.Root, null, null, NoColumns).Rows;

        Assert.Equal("月曜日", weekdays[0].Label);          // Mon→Sun ordering
        Assert.Equal(3, Row(weekdays, "月曜日").Count);     // 05/11, 05/18, 08/03
        Assert.Equal(6, weekdays.Sum(r => r.Count));
    }

    [Fact]
    public void Weekday_responses_list_the_individual_surveys()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // Mondays (dim_date day_of_week 0): 2026/05/11, 2026/05/18, 2026/08/03.
        var monday = analytics.ResponsesForWeekday(pid, 0);
        Assert.Equal(3, monday.Count);
        Assert.Contains(monday, r => r["記入日"] == "2026/05/11");
        Assert.All(monday, r => Assert.True(r.ContainsKey("ご意見")));
    }
}
