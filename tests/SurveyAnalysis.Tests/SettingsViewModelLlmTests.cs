using SurveyAnalysis.Data;
using SurveyAnalysis.ViewModels;
using Xunit;

namespace SurveyAnalysis.Tests;

// The LLM / embedding settings round-trip through storage; the shared API key is protected at rest;
// and the fields whose defaults were removed reset to empty.
public class SettingsViewModelLlmTests
{
    [Fact]
    public void Llm_settings_round_trip()
    {
        using var temp = new TempDatabase();
        var store = new SettingsRepository(temp.Db);

        var vm = new SettingsViewModel(store)
        {
            Endpoint = "http://localhost:1234/v1",
            ApiKey = "sk-test",
            EmbeddingModel = "text-embedding-3-large",
            LlmConcurrency = "8",
            LlmEmbeddingBatchSize = "32",
            LlmRequestTimeoutSeconds = "60",
        };
        vm.Save();

        var reloaded = new SettingsViewModel(store);
        Assert.Equal("http://localhost:1234/v1", reloaded.Endpoint);
        Assert.Equal("sk-test", reloaded.ApiKey);
        Assert.Equal("text-embedding-3-large", reloaded.EmbeddingModel);
        Assert.Equal("8", reloaded.LlmConcurrency);
        Assert.Equal("32", reloaded.LlmEmbeddingBatchSize);
        Assert.Equal("60", reloaded.LlmRequestTimeoutSeconds);
    }

    [Fact]
    public void Api_key_is_stored_protected()
    {
        using var temp = new TempDatabase();
        var store = new SettingsRepository(temp.Db);
        new SettingsViewModel(store) { ApiKey = "sk-secret" }.Save();

        var raw = store.LoadAll()["ApiKey"];
        Assert.NotEqual("sk-secret", raw);                          // not stored as plaintext
        Assert.Equal("sk-secret", SecretProtector.Unprotect(raw));  // but recoverable
    }

    [Fact]
    public void Reset_keeps_user_values_for_fields_without_defaults()
    {
        using var temp = new TempDatabase();
        var vm = new SettingsViewModel(new SettingsRepository(temp.Db))
        {
            CompanyName = "○○社",
            MailFrom = "a@example.com",
            MailTo = "b@example.com",
            ApiKey = "sk-x",
            GmailAddress = "me@gmail.com",
            SmtpPassword = "pw",
            EmbeddingModel = "m",
            LlmConcurrency = "99",
        };

        vm.ResetToDefaultsCommand.Execute(null);

        // No-default (user-supplied) fields are left untouched.
        Assert.Equal("○○社", vm.CompanyName);
        Assert.Equal("a@example.com", vm.MailFrom);
        Assert.Equal("b@example.com", vm.MailTo);
        Assert.Equal("sk-x", vm.ApiKey);
        Assert.Equal("me@gmail.com", vm.GmailAddress);
        Assert.Equal("pw", vm.SmtpPassword);
        // Fields that do have defaults are restored.
        Assert.Equal("text-embedding-3-small", vm.EmbeddingModel);
        Assert.Equal("4", vm.LlmConcurrency);
        Assert.Equal("https://api.openai.com/v1", vm.Endpoint);
    }
}
