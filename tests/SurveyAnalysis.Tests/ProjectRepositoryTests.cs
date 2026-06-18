using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

public class ProjectRepositoryTests
{
    [Fact]
    public void Insert_then_Load_round_trips_fields_in_order()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        var project = new Project { Name = "○○ケーブル 工事アンケート" };
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name, Analysis = AnalysisMethod.None });
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, Analysis = AnalysisMethod.None, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見・ご要望", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });

        var id = repo.Insert(project);

        Assert.True(id > 0);
        Assert.Equal(id, project.Id);

        var loaded = repo.Load(id);
        Assert.NotNull(loaded);
        Assert.Equal("○○ケーブル 工事アンケート", loaded!.Name);

        // Field order and values survive the round trip.
        Assert.Equal(3, loaded.Fields.Count);
        Assert.Equal("氏名", loaded.Fields[0].Name);
        Assert.Equal(FieldType.Name, loaded.Fields[0].FieldType);

        var date = loaded.Fields[1];
        Assert.Equal(FieldType.Date, date.FieldType);
        Assert.True(date.UseForAggregation);
        Assert.True(date.UseLoadDateAsDefault); // aggregation forces this on

        var sentiment = loaded.Fields[2];
        Assert.Equal(AnalysisMethod.Sentiment, sentiment.Analysis);
    }

    [Fact]
    public void Description_round_trips_through_insert_load_and_update()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        var project = new Project { Name = "説明あり", Description = "工事後アンケート。自由記述を分析する。" };
        var id = repo.Insert(project);
        Assert.Equal("工事後アンケート。自由記述を分析する。", repo.Load(id)!.Description);

        var draft = new Project { Id = id, Name = "説明あり", Description = "更新後の説明" };
        repo.Update(draft);
        Assert.Equal("更新後の説明", repo.Load(id)!.Description);
    }

    [Fact]
    public void Insert_rejects_a_duplicate_project_name()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        repo.Insert(new Project { Name = "同名プロジェクト" });

        // The projects.name unique index (v3) is the integrity backstop behind the dialog's pre-check.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => repo.Insert(new Project { Name = "同名プロジェクト" }));
    }

    [Fact]
    public void ListSummaries_counts_fields_and_orders_newest_first()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        var first = new Project { Name = "先に作った" };
        first.Fields.Add(new DataField { Name = "f1" });
        first.Fields.Add(new DataField { Name = "f2" });
        repo.Insert(first);

        var second = new Project { Name = "後で作った" };
        second.Fields.Add(new DataField { Name = "f1" });
        repo.Insert(second);

        var summaries = repo.ListSummaries();

        Assert.Equal(2, summaries.Count);
        Assert.Equal("後で作った", summaries[0].Name);
        Assert.Equal(1, summaries[0].FieldCount);
        Assert.Equal("先に作った", summaries[1].Name);
        Assert.Equal(2, summaries[1].FieldCount);
    }

    [Fact]
    public void Delete_removes_project_and_cascades_fields()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        var project = new Project { Name = "消すプロジェクト" };
        project.Fields.Add(new DataField { Name = "f1" });
        var id = repo.Insert(project);

        repo.Delete(id);

        Assert.Null(repo.Load(id));
        Assert.Empty(repo.ListSummaries());
    }

    [Fact]
    public void Load_returns_null_for_unknown_id()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        Assert.Null(repo.Load(999));
    }

    [Fact]
    public void Update_replaces_fields_and_name()
    {
        using var temp = new TempDatabase();
        var repo = new ProjectRepository(temp.Db);

        var original = new Project { Name = "元の名前" };
        original.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        original.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        var id = repo.Insert(original);

        // Edited draft: rename, drop 記入日, add a sentiment field — carrying the existing id.
        var draft = new Project { Id = id, Name = "新しい名前" };
        draft.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        draft.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        repo.Update(draft);

        var loaded = repo.Load(id);
        Assert.NotNull(loaded);
        Assert.Equal("新しい名前", loaded!.Name);

        // Fields are replaced with the edited set, in order.
        Assert.Equal(2, loaded.Fields.Count);
        Assert.Equal("氏名", loaded.Fields[0].Name);
        Assert.Equal("ご意見", loaded.Fields[1].Name);
        Assert.Equal(AnalysisMethod.Sentiment, loaded.Fields[1].Analysis);
    }
}
