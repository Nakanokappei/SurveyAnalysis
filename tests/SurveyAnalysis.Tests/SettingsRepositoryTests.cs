using System.Collections.Generic;
using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

public class SettingsRepositoryTests
{
    [Fact]
    public void Save_then_LoadAll_round_trips()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Db);

        repo.Save(new Dictionary<string, string>
        {
            ["CompanyName"] = "○○ケーブル株式会社",
            ["SmtpPort"] = "587",
        });

        var values = repo.LoadAll();
        Assert.Equal("○○ケーブル株式会社", values["CompanyName"]);
        Assert.Equal("587", values["SmtpPort"]);
    }

    [Fact]
    public void Save_upserts_existing_keys_and_leaves_others()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Db);

        repo.Save(new Dictionary<string, string> { ["CompanyName"] = "旧名", ["SmtpPort"] = "587" });
        repo.Save(new Dictionary<string, string> { ["SmtpPort"] = "465" });

        var values = repo.LoadAll();
        Assert.Equal("465", values["SmtpPort"]);   // overwritten
        Assert.Equal("旧名", values["CompanyName"]); // untouched
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_round_trips_and_never_stores_bare()
    {
        const string secret = "app-password-1234";

        var stored = SecretProtector.Protect(secret);

        Assert.NotEqual(secret, stored);
        Assert.Equal(secret, SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Empty_secret_round_trips_to_empty()
    {
        Assert.Equal("", SecretProtector.Unprotect(SecretProtector.Protect("")));
    }
}
