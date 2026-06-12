using System.Linq;
using System.Text;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;
using Xunit;

namespace SurveyAnalysis.Tests;

// FinishProjectFromCsv is the orchestration behind "CSV からプロジェクトを作る": it persists the
// reviewed schema, imports the CSV's rows as responses, rebuilds the analytics star, and opens the
// new project's dashboard. This drives it headlessly through real repositories.
public class MainWindowViewModelTests
{
    [Fact]
    public void FinishProjectFromCsv_persists_the_project_imports_rows_and_opens_the_dashboard()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var settings = new SettingsRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);
        var shell = new MainWindowViewModel(projects, settings, responses, analytics);

        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes(
            "記入日,満足度,ご意見\n2026/05/20,5,良かった\n2026/08/03,4,普通\n"));

        // The design dialog hands back a project built from the (reviewed) inferred fields.
        var project = new Project { Name = "工事アンケート" };
        foreach (var field in CsvProjectImport.InferFields(csv))
            project.Fields.Add(field);

        shell.FinishProjectFromCsv(project, csv, "工事アンケート.csv");

        // The project was saved (assigned an id) and the two rows landed as responses.
        Assert.True(project.Id > 0);
        Assert.Equal(2, responses.CountForProject(project.Id));

        // The new project is open on its dashboard.
        Assert.Same(project, shell.CurrentProject);
        Assert.IsType<DashboardViewModel>(shell.CurrentPage);

        // The analytics star was rebuilt as part of the flow: the time slice groups both responses
        // (both fall in 2026 年度) without anyone opening a slice first.
        var table = analytics.AggregateRows(
            project.Id, AnalysisGrouping.Time, TimeScope.Root, null, null, System.Array.Empty<AnalysisColumn>());
        Assert.Equal(2, table.Total.Count);
    }
}
