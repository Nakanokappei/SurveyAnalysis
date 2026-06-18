using System;
using System.Linq;
using System.Text;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;
using Xunit;

namespace SurveyAnalysis.Tests;

// The import flow must rebuild the analytics star as part of the merge, so the time/region/topic
// slices reflect the freshly imported rows without first opening a slice. These tests drive the
// view model headlessly (no UI): load a CSV, map every column, run Merge, then read the star back.
public class ImportViewModelTests
{
    private static (TempDatabase temp, Project project, ResponseRepository responses, AnalyticsRepository analytics) Setup()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "工事アンケート" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "都道府県", FieldType = FieldType.Address });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        projects.Insert(project);

        return (temp, projects.Load(project.Id)!, responses, analytics);
    }

    [Fact]
    public void Merge_persists_responses_and_rebuilds_the_analytics_star()
    {
        var (temp, project, responses, analytics) = Setup();
        using var _ = temp;

        var vm = new ImportViewModel(project, responses, analytics);
        vm.LoadCsv(Encoding.UTF8.GetBytes("記入日,都道府県,ご意見\n2026/05/20,東京都,良かった\n2026/08/03,大阪府,普通\n"), "t.csv");

        // Map every column onto its same-named project field (every column must be mapped to merge).
        vm.Columns[0].SelectedMapping = "記入日";
        vm.Columns[1].SelectedMapping = "都道府県";
        vm.Columns[2].SelectedMapping = "ご意見";

        Assert.True(vm.MergeCommand.CanExecute(null));
        vm.MergeCommand.Execute(null);

        // Raw responses landed.
        Assert.Equal(2, responses.CountForProject(project.Id));

        // The star was rebuilt during the merge: the time slice groups the two responses (both 2026
        // 年度) without anyone opening a slice first. An empty result here would mean the ETL wasn't
        // wired into import.
        var noColumns = Array.Empty<AnalysisColumn>();
        var byYear = analytics.AggregateRows(project.Id, AnalysisGrouping.Time, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal(new[] { ("2026年度", 2) }, byYear.Select(r => (r.Label, r.Count)).ToArray());

        // And the region slice reflects the prefecture column.
        var byRegion = analytics.AggregateRows(project.Id, AnalysisGrouping.Region, TimeScope.Root, null, null, noColumns).Rows;
        Assert.Equal(2, byRegion.Count);
        Assert.Contains(byRegion, r => r.Label == "東京都" && r.Count == 1);
        Assert.Contains(byRegion, r => r.Label == "大阪府" && r.Count == 1);
    }

    [Fact]
    public void Merge_raises_Merged_with_the_project_so_the_host_can_analyse()
    {
        var (temp, project, responses, analytics) = Setup();
        using var _ = temp;

        var vm = new ImportViewModel(project, responses, analytics);
        vm.LoadCsv(Encoding.UTF8.GetBytes("記入日,都道府県,ご意見\n2026/05/20,東京都,良かった\n"), "t.csv");
        vm.Columns[0].SelectedMapping = "記入日";
        vm.Columns[1].SelectedMapping = "都道府県";
        vm.Columns[2].SelectedMapping = "ご意見";

        Project? merged = null;
        vm.Merged += p => merged = p;
        vm.MergeCommand.Execute(null);

        Assert.Same(project, merged);
    }

    [Fact]
    public void Merge_does_not_raise_Merged_when_nothing_is_imported()
    {
        var (temp, project, responses, analytics) = Setup();
        using var _ = temp;

        var vm = new ImportViewModel(project, responses, analytics);
        vm.LoadCsv(Encoding.UTF8.GetBytes("記入日,都道府県,ご意見\n2026/05/20,東京都,良かった\n"), "t.csv");
        // Every column set to 取り込まない: merge is allowed (all mapped) but yields no responses.
        foreach (var column in vm.Columns)
            column.SelectedMapping = "（取り込まない）";

        var raised = false;
        vm.Merged += _ => raised = true;
        vm.MergeCommand.Execute(null);

        Assert.False(raised);
        Assert.Equal(0, responses.CountForProject(project.Id));
    }
}
