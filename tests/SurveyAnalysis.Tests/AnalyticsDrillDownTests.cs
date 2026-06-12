using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// Drill-down ETL queries behind the 時間別 analysis view: 全期間→年度→月→週→日→個票. The seed straddles
// two fiscal years (April-start) and puts two responses in one ISO week so the week/day split is
// observable. 2026/05/11 is a Monday, so 05/11 and 05/12 share an ISO week and 05/18 starts the next.
public class AnalyticsDrillDownTests
{
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
            Response("2026/08/03", "夏"),         // FY2026, 2026年8月
            Response("2026/02/14", "冬"),         // FY2025 (Jan–Mar → 前年度)
            Response("2025/12/25", "年末"),       // FY2025
        });

        analytics.Rebuild(project);
        return (temp, analytics, pid);
    }

    private static TimeChild Child(IReadOnlyList<TimeChild> children, string label) =>
        children.Single(c => c.Label == label);

    [Fact]
    public void Root_children_are_fiscal_years()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        var years = analytics.DrillTimeChildren(pid, TimeScope.Root);

        Assert.Equal(4, Child(years, "2026年度").Count);
        Assert.Equal(2, Child(years, "2025年度").Count);
        Assert.Equal(6, years.Sum(c => c.Count)); // parent total = sum of children
    }

    [Fact]
    public void Drill_year_to_month_then_month_to_week_then_week_to_day()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // 年度 → 月
        var fy2026 = Child(analytics.DrillTimeChildren(pid, TimeScope.Root), "2026年度");
        var months = analytics.DrillTimeChildren(pid, fy2026.Scope);
        Assert.Equal(3, Child(months, "2026年5月").Count);
        Assert.Equal(1, Child(months, "2026年8月").Count);

        // 月 → 週: 2026年5月 splits into two ISO weeks summing to 3 (one week has the 11th & 12th).
        var may = Child(months, "2026年5月");
        var weeks = analytics.DrillTimeChildren(pid, may.Scope);
        Assert.Equal(2, weeks.Count);
        Assert.Equal(3, weeks.Sum(w => w.Count));
        var weekOfTwo = weeks.Single(w => w.Count == 2);

        // 週 → 日: that week breaks into the two individual dates.
        var days = analytics.DrillTimeChildren(pid, weekOfTwo.Scope);
        Assert.Equal(2, days.Count);
        Assert.Equal(1, Child(days, "2026-05-11").Count);
        Assert.Equal(1, Child(days, "2026-05-12").Count);
    }

    [Fact]
    public void Day_scope_lists_its_individual_responses()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        var fy2026 = Child(analytics.DrillTimeChildren(pid, TimeScope.Root), "2026年度");
        var may = Child(analytics.DrillTimeChildren(pid, fy2026.Scope), "2026年5月");
        var week = analytics.DrillTimeChildren(pid, may.Scope).Single(w => w.Count == 2);
        var may11 = Child(analytics.DrillTimeChildren(pid, week.Scope), "2026-05-11");

        Assert.True(may11.Scope.IsTerminal);

        var rows = analytics.ResponsesForScope(pid, may11.Scope);
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
        var years = analytics.DrillTimeChildren(pid, TimeScope.Root, 20260501, 20260531);
        Assert.Single(years);
        Assert.Equal("2026年度", years[0].Label);
        Assert.Equal(3, years[0].Count);

        var dow = analytics.DayOfWeekForScope(pid, TimeScope.Root, 20260501, 20260531);
        Assert.Equal(3, dow.Sum(g => g.Count));
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

    [Fact]
    public void Day_of_week_table_reflects_the_scope()
    {
        var (temp, analytics, pid) = Seed();
        using var _ = temp;

        // Whole dataset: every response counts once across the weekday buckets.
        var all = analytics.DayOfWeekForScope(pid, TimeScope.Root);
        Assert.Equal(6, all.Sum(g => g.Count));

        // Scoped to 2026年5月: only that month's three responses (Mon ×2 on 11th/18th, Tue ×1 on 12th).
        var fy2026 = Child(analytics.DrillTimeChildren(pid, TimeScope.Root), "2026年度");
        var may = Child(analytics.DrillTimeChildren(pid, fy2026.Scope), "2026年5月");
        var mayDow = analytics.DayOfWeekForScope(pid, may.Scope).ToDictionary(g => g.Label, g => g.Count);
        Assert.Equal(2, mayDow["月曜日"]);
        Assert.Equal(1, mayDow["火曜日"]);
        Assert.Equal(3, mayDow.Values.Sum());
    }
}
