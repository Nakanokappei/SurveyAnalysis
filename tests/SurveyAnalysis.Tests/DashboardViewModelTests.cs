using System;
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

        var project = new Project { Name = "工事アンケート" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        project.Months.Add("2026年5月");
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("氏名", "田中太郎"), ("ご意見", "料金プランの資料がほしい。")),
            Response(("記入日", "2026/05/21"), ("氏名", "佐藤花子"), ("ご意見", "助かった。")),
            Response(("記入日", "2026/04/10"), ("氏名", "別月さん"), ("ご意見", "先月分。")),
        });

        var vm = new DashboardViewModel(projects.Load(pid)!, responses);
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
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Months.Add("2026年5月");
        var pid = projects.Insert(project);

        var vm = new DashboardViewModel(projects.Load(pid)!, responses);
        vm.SetRange(DateRangePreset.Custom, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        Assert.Equal(0, vm.TotalResponses);
        Assert.True(vm.HasNoResponses);
        Assert.Empty(vm.Rows);
    }
}
