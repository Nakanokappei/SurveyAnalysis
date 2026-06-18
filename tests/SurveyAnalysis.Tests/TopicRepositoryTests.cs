using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The per-column topic dictionary (field_topics): CRUD, the per-field uniqueness constraint, the
// clustering replace, and cascade when the owning field is removed.
public class TopicRepositoryTests
{
    // Inserts a project with one 自由記述 field and returns that field's (stamped) id.
    private static long FreeTextFieldId(TempDatabase temp)
    {
        var project = new Project { Name = "P" };
        var field = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(field);
        new ProjectRepository(temp.Db).Insert(project);
        return field.Id;
    }

    [Fact]
    public void Add_list_rename_delete_round_trip()
    {
        using var temp = new TempDatabase();
        var repo = new TopicRepository(temp.Db);
        var fieldId = FreeTextFieldId(temp);

        var wiringId = repo.AddTopic(fieldId, "配線");
        repo.AddTopic(fieldId, "対応");
        Assert.Equal(new[] { "対応", "配線" }, repo.ListTopics(fieldId).Select(t => t.Label));   // ordered by label

        repo.RenameTopic(wiringId, "配線・接続");
        Assert.Contains(repo.ListTopics(fieldId), t => t.Label == "配線・接続");

        repo.DeleteTopic(wiringId);
        Assert.Single(repo.ListTopics(fieldId));
        Assert.DoesNotContain(repo.ListTopics(fieldId), t => t.Label == "配線・接続");
    }

    [Fact]
    public void Duplicate_label_within_a_field_is_rejected()
    {
        using var temp = new TempDatabase();
        var repo = new TopicRepository(temp.Db);
        var fieldId = FreeTextFieldId(temp);

        repo.AddTopic(fieldId, "同名");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => repo.AddTopic(fieldId, "同名"));
    }

    [Fact]
    public void ReplaceTopics_swaps_the_set_and_round_trips_centroids()
    {
        using var temp = new TempDatabase();
        var repo = new TopicRepository(temp.Db);
        var fieldId = FreeTextFieldId(temp);
        repo.AddTopic(fieldId, "古い");

        repo.ReplaceTopics(fieldId, new (string, float[]?)[]
        {
            ("新A", new[] { 0.1f, 0.2f, 0.3f }),
            ("新B", null),
        });

        var topics = repo.ListTopics(fieldId);
        Assert.Equal(new[] { "新A", "新B" }, topics.Select(t => t.Label));
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, topics.Single(t => t.Label == "新A").Centroid);
        Assert.Null(topics.Single(t => t.Label == "新B").Centroid);
    }

    [Fact]
    public void Topics_cascade_away_when_the_field_is_removed()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);

        var project = new Project { Name = "P" };
        var field = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(field);
        projects.Insert(project);
        topics.AddTopic(field.Id, "T");

        // A schema edit that drops the field deletes it; field_topics cascade with it.
        projects.Update(new Project { Id = project.Id, Name = "P" });
        Assert.Empty(topics.ListTopics(field.Id));
    }
}
