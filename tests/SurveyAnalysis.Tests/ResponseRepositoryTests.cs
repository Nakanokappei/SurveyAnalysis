using System.Collections.Generic;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

public class ResponseRepositoryTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers)
    {
        var list = new List<FieldAnswer>();
        foreach (var (field, value) in answers)
            list.Add(new FieldAnswer(field, value));
        return new SurveyResponse { Answers = list };
    }

    [Fact]
    public void InsertResponses_then_CountForProject()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "test.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("ご意見", "良かった")),
            Response(("記入日", "2026/05/21"), ("ご意見", "残念だった")),
        });

        Assert.Equal(2, responses.CountForProject(pid));
    }

    [Fact]
    public void Responses_survive_a_schema_edit()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);
        responses.InsertResponses(pid, "x.csv", new[] { Response(("ご意見", "a")) });

        // A schema edit deletes and reinserts field rows; responses link by name, not field id,
        // so they must not be cascade-deleted.
        var draft = new Project { Id = pid, Name = "P2" };
        draft.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        draft.Fields.Add(new DataField { Name = "電話", FieldType = FieldType.Phone });
        projects.Update(draft);

        Assert.Equal(1, responses.CountForProject(pid));
    }

    [Fact]
    public void LoadForProject_returns_field_value_maps_newest_first()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);

        responses.InsertResponses(pid, "t.csv", new[]
        {
            Response(("記入日", "2026/05/20"), ("ご意見", "古い")),
            Response(("記入日", "2026/05/21"), ("ご意見", "新しい")),
        });

        var loaded = responses.LoadForProject(pid);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("新しい", loaded[0]["ご意見"]);   // newest (highest id) first
        Assert.Equal("2026/05/21", loaded[0]["記入日"]);
        Assert.Equal("古い", loaded[1]["ご意見"]);
    }

    [Fact]
    public void Responses_are_removed_with_the_project()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        var pid = projects.Insert(project);
        responses.InsertResponses(pid, "x.csv", new[] { Response(("ご意見", "a")) });

        projects.Delete(pid);

        Assert.Equal(0, responses.CountForProject(pid));
    }
}
