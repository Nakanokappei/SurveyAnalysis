using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// With a real protector wired in, PII field values are encrypted at rest in answers (and the OCR staging
// JSON) but read back transparently as plaintext; non-PII values stay plaintext. The address ETL still works
// because the analytics layer decrypts on read.
public class PiiEncryptionTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    // The raw (undecrypted) stored value for one field's first answer, read straight from the table.
    private static string RawValue(AppDatabase db, string fieldName)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.value FROM answers a JOIN fields f ON f.id = a.field_id WHERE f.name = $n LIMIT 1;";
        command.Parameters.AddWithValue("$n", fieldName);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void Pii_values_are_encrypted_at_rest_and_read_back_as_plaintext()
    {
        using var temp = new TempDatabase();
        var protector = DataKeyStore.Load(temp.Db);
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db, protector);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });          // PII
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });     // non-PII
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Response(("氏名", "山田 太郎"), ("記入日", "2026/05/10"), ("ご意見", "良かった")),
        });

        // At rest: the PII value is ciphertext; the non-PII value is plaintext.
        Assert.StartsWith("enc1:", RawValue(temp.Db, "氏名"));
        Assert.DoesNotContain("山田", RawValue(temp.Db, "氏名"));
        Assert.Equal("良かった", RawValue(temp.Db, "ご意見"));

        // On read: the PII value comes back decrypted.
        var loaded = responses.LoadForProject(project.Id).Single();
        Assert.Equal("山田 太郎", loaded["氏名"]);
        Assert.Equal("良かった", loaded["ご意見"]);
    }

    [Fact]
    public void Encrypted_address_still_drives_the_region_etl()
    {
        using var temp = new TempDatabase();
        var protector = DataKeyStore.Load(temp.Db);
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db, protector);
        var analytics = new AnalyticsRepository(temp.Db, protector);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "住所", FieldType = FieldType.Address });   // PII
        projects.Insert(project);

        responses.InsertResponses(project.Id, "t", new[]
        {
            Response(("記入日", "2026/05/10"), ("住所", "東京都新宿区西新宿1-1")),
            Response(("記入日", "2026/05/11"), ("住所", "東京都渋谷区道玄坂2-2")),
        });

        // The address is encrypted at rest …
        Assert.StartsWith("enc1:", RawValue(temp.Db, "住所"));

        // … yet the analytics ETL decrypts it, so the region groups by 東京都 (2 responses).
        analytics.Rebuild(project);
        var table = analytics.AggregateRows(project.Id, AnalysisGrouping.Region, TimeScope.Root, null, null, PeriodScopedCountColumn());
        Assert.Equal("東京都", table.Rows[0].Label);
        Assert.Equal(2, table.Rows[0].Count);
    }

    [Fact]
    public void Staging_values_json_is_encrypted_at_rest_and_decoded_on_read()
    {
        using var temp = new TempDatabase();
        var protector = DataKeyStore.Load(temp.Db);
        var projects = new ProjectRepository(temp.Db);
        var staging = new ImageStagingRepository(temp.Db, protector);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });
        projects.Insert(project);

        staging.Add(project.Id, "scan.png", "image/png", new byte[] { 1, 2, 3 },
            new System.Collections.Generic.Dictionary<string, string> { ["氏名"] = "山田 太郎" });

        // At rest the values JSON is ciphertext (no plaintext name leaks).
        using (var connection = temp.Db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT values_json FROM image_import_staging LIMIT 1;";
            var raw = (string)command.ExecuteScalar()!;
            Assert.StartsWith("enc1:", raw);
            Assert.DoesNotContain("山田", raw);
        }

        // On read it decodes back to the original map.
        var staged = staging.ListForProject(project.Id).Single();
        Assert.Equal("山田 太郎", staged.Values["氏名"]);
    }

    // The single 件数 column used by the plain region report (mirrors PeriodScopedViewModel.CountColumn).
    private static System.Collections.Generic.IReadOnlyList<AnalysisColumn> PeriodScopedCountColumn() =>
        new[] { new AnalysisColumn("件数", FieldAggregation.Count) };
}
