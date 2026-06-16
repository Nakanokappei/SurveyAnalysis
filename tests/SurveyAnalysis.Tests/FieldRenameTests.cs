using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// Answers reference their field by id, so a schema edit that renames a field keeps the imported
// answers attached and they re-surface under the new name. Removing a field instead cascade-deletes
// its answers. These guard the EAV→field_id change (the old name-keyed answers broke on rename).
public class FieldRenameTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    [Fact]
    public void Renaming_a_field_keeps_its_answers()
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
            Response(("記入日", "2026/05/11"), ("ご意見", "とても満足")),
            Response(("記入日", "2026/05/12"), ("ご意見", "普通")),
        });

        // Edit the schema: rename ご意見 → ご感想 in place (the design dialog carries each field's id).
        var draft = projects.Load(pid)!;
        draft.Fields[1].Name = "ご感想";
        projects.Update(draft);

        var reloaded = projects.Load(pid)!;
        Assert.Equal("ご感想", reloaded.Fields[1].Name);

        // The answers survived the rename and now read under the current name, not the old one.
        var loaded = responses.LoadForProject(pid);
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, m => Assert.True(m.ContainsKey("ご感想")));
        Assert.DoesNotContain(loaded, m => m.ContainsKey("ご意見"));

        // And aggregation resolves them under the new column (種類数 = 2 distinct opinions).
        analytics.Rebuild(reloaded);
        var columns = reloaded.Fields.Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f))).ToList();
        var year = analytics.AggregateRows(pid, AnalysisGrouping.Time, TimeScope.Root, null, null, columns).Rows.Single();
        Assert.Equal("2", year.Cells[1]);
    }

    [Fact]
    public void Removing_a_field_deletes_its_answers()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/11"), ("ご意見", "とても満足")),
            Response(("記入日", "2026/05/12"), ("ご意見", "普通")),
        });

        // Drop ご意見 from the schema; its answers cascade away while the responses remain.
        var draft = projects.Load(pid)!;
        draft.Fields.RemoveAt(1);
        projects.Update(draft);

        var loaded = responses.LoadForProject(pid);
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, m => Assert.True(m.ContainsKey("記入日")));
        Assert.All(loaded, m => Assert.False(m.ContainsKey("ご意見")));
    }
}
