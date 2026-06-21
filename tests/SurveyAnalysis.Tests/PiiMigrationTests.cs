using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// PiiMigration.EncryptExisting encrypts PII left in plaintext from before encryption was added — PII fields
// only, idempotently (a flag + skipping already-encrypted values), and only when the protector is unlocked.
public class PiiMigrationTests
{
    private static SurveyResponse Response(params (string Field, string Value)[] answers) =>
        new() { Answers = answers.Select(a => new FieldAnswer(a.Field, a.Value)).ToList() };

    private static string RawValue(AppDatabase db, string fieldName)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.value FROM answers a JOIN fields f ON f.id = a.field_id WHERE f.name = $n LIMIT 1;";
        command.Parameters.AddWithValue("$n", fieldName);
        return (string)command.ExecuteScalar()!;
    }

    private static (TempDatabase temp, Project project) SeedPlaintext()
    {
        var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var plain = new ResponseRepository(temp.Db);   // no protector → stores plaintext (the legacy state)

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name });          // PII
        project.Fields.Add(new DataField { Name = "ご意見", FieldType = FieldType.FreeText });     // non-PII
        projects.Insert(project);
        plain.InsertResponses(project.Id, "t", new[] { Response(("氏名", "山田 太郎"), ("ご意見", "良い")) });
        return (temp, project);
    }

    [Fact]
    public void Encrypts_existing_plaintext_pii_only_and_reads_back()
    {
        var (temp, project) = SeedPlaintext();
        using var _t = temp;
        Assert.Equal("山田 太郎", RawValue(temp.Db, "氏名"));   // legacy plaintext

        var protector = DataKeyStore.Load(temp.Db);
        PiiMigration.EncryptExisting(temp.Db, protector);

        Assert.StartsWith("enc1:", RawValue(temp.Db, "氏名"));   // PII now encrypted at rest
        Assert.Equal("良い", RawValue(temp.Db, "ご意見"));        // non-PII untouched

        var loaded = new ResponseRepository(temp.Db, protector).LoadForProject(project.Id).Single();
        Assert.Equal("山田 太郎", loaded["氏名"]);                // reads back decrypted
    }

    [Fact]
    public void Is_idempotent()
    {
        var (temp, _) = SeedPlaintext();
        using var _t = temp;
        var protector = DataKeyStore.Load(temp.Db);

        PiiMigration.EncryptExisting(temp.Db, protector);
        var afterFirst = RawValue(temp.Db, "氏名");
        PiiMigration.EncryptExisting(temp.Db, protector);   // second pass

        Assert.Equal(afterFirst, RawValue(temp.Db, "氏名"));   // not double-encrypted
        Assert.Equal("山田 太郎", protector.Decode(RawValue(temp.Db, "氏名")));
    }

    [Fact]
    public void A_locked_protector_leaves_plaintext_alone()
    {
        var (temp, _) = SeedPlaintext();
        using var _t = temp;

        PiiMigration.EncryptExisting(temp.Db, new DpapiDataProtector(null));   // locked → no-op

        Assert.Equal("山田 太郎", RawValue(temp.Db, "氏名"));   // still plaintext (nothing this user can do)
    }
}
