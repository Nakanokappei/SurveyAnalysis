using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The image-OCR staging area: rows hold the image bytes + OCR'd values until reviewed, round-trip
// intact, and are removed individually (confirm/discard) and by project cascade.
public class ImageStagingRepositoryTests
{
    [Fact]
    public void Stage_list_and_delete_round_trips_bytes_and_values()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var staging = new ImageStagingRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        projects.Insert(project);

        var bytes = new byte[] { 1, 2, 3, 250, 0, 99 };
        var values = new Dictionary<string, string> { ["氏名"] = "山田 太郎", ["記入日"] = "2026/05/20" };
        var id = staging.Add(project.Id, "form1.png", "image/png", bytes, values);

        Assert.Equal(1, staging.CountForProject(project.Id));
        var staged = Assert.Single(staging.ListForProject(project.Id));
        Assert.Equal(id, staged.Id);
        Assert.Equal("form1.png", staged.SourceName);
        Assert.Equal("image/png", staged.MediaType);
        Assert.Equal(bytes, staged.ImageBytes);                  // BLOB preserved byte-for-byte
        Assert.Equal("山田 太郎", staged.Values["氏名"]);          // values JSON round-trips
        Assert.Equal("2026/05/20", staged.Values["記入日"]);

        staging.Delete(id);
        Assert.Empty(staging.ListForProject(project.Id));
    }

    [Fact]
    public void Staging_rows_cascade_away_with_their_project()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var staging = new ImageStagingRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });
        projects.Insert(project);
        staging.Add(project.Id, "a.jpg", "image/jpeg", new byte[] { 9 }, new Dictionary<string, string> { ["ご意見"] = "良い" });

        projects.Delete(project.Id);
        Assert.Equal(0, staging.CountForProject(project.Id));
    }
}
