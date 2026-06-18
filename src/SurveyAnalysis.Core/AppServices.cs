using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using SurveyAnalysis.Data;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;

namespace SurveyAnalysis;

// The composition root. A desktop app this small does not need a DI container; this single static
// wiring point builds the database and repositories once and hands them out. The shell and the
// settings dialog both reach their repositories through here (the running app via App.axaml.cs, the
// XAML design-time DataContext via MainWindowViewModel's parameterless constructor).
public static class AppServices
{
    private static readonly AppDatabase Database = CreateDatabase();

    public static readonly ProjectRepository Projects = new(Database);
    public static readonly SettingsRepository Settings = new(Database);
    public static readonly ResponseRepository Responses = new(Database);
    public static readonly AnalyticsRepository Analytics = new(Database);
    public static readonly TopicRepository Topics = new(Database);

    // Backup / optimize / restore for the database file (driven from the settings dialog and on close).
    public static readonly DatabaseMaintenance Maintenance = new(Database.DatabaseFilePath);

    // The LLM/embedding stack: a separate on-disk cache and the shared OpenAI-compatible client built
    // from the persisted settings. Feature consumers (sentiment / topics / OCR / report) depend only
    // on the ILlmClient seam. Settings changes take effect on next launch (the client is built once,
    // matching the rest of this static composition root); the cache is safe across rebuilds because
    // provider identity is part of every cache key.
    private static readonly LlmCacheDatabase CacheDatabase = CreateCacheDatabase();
    public static readonly ILlmCache LlmCache = new SqliteLlmCache(CacheDatabase);
    public static readonly ILlmClient Llm = CreateLlmClient();

    private static AppDatabase CreateDatabase()
    {
        var database = AppDatabase.Default();
        database.EnsureSchema();
        return database;
    }

    private static LlmCacheDatabase CreateCacheDatabase()
    {
        var database = LlmCacheDatabase.Default();
        database.EnsureSchema();
        return database;
    }

    private static ILlmClient CreateLlmClient()
    {
        var values = Settings.LoadAll();

        // One shared connection (endpoint + key) for both chat and embeddings; only the model differs.
        // Chat carries its model per request, so the chat config's DefaultModel is just a label.
        var endpoint = Get(values, "Endpoint", "https://api.openai.com/v1");
        var apiKey = SecretProtector.Unprotect(Get(values, "ApiKey", ""));
        var chat = new LlmProviderConfig(endpoint, apiKey, "(chat)");
        var embedding = new LlmProviderConfig(endpoint, apiKey, Get(values, "EmbeddingModel", "text-embedding-3-small"));

        var options = new LlmOptions
        {
            MaxConcurrency = ParsePositive(Get(values, "LlmConcurrency", "4"), 4),
            EmbeddingBatchSize = ParsePositive(Get(values, "LlmEmbeddingBatchSize", "64"), 64),
            RequestTimeout = TimeSpan.FromSeconds(ParsePositive(Get(values, "LlmRequestTimeoutSeconds", "100"), 100)),
        };

        // One shared HttpClient; per-request timeouts are enforced inside the client via a linked CTS,
        // so the handler timeout is disabled here.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAiCompatibleClient(http, chat, embedding, options, LlmCache);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) ? value : fallback;

    private static int ParsePositive(string text, int fallback)
        => int.TryParse(text, out var n) && n > 0 ? n : fallback;
}
