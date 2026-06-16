using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// The SQLite LLM cache: round-trips embeddings and chat results, misses cleanly, persists across
// reopen, and tolerates duplicate writes.
public class SqliteLlmCacheTests
{
    [Fact]
    public void Embedding_round_trips_exactly()
    {
        using var temp = new TempCacheDatabase();
        var cache = new SqliteLlmCache(temp.Db);
        var vector = new[] { 0.5f, -1.25f, 3.0f, 0f };

        cache.PutEmbedding("k1", "prov", "m", vector);

        Assert.True(cache.TryGetEmbedding("k1", out var got));
        Assert.Equal(vector, got);
        Assert.False(cache.TryGetEmbedding("missing", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Chat_round_trips_and_marks_from_cache()
    {
        using var temp = new TempCacheDatabase();
        var cache = new SqliteLlmCache(temp.Db);
        cache.PutChat("c1", "prov", "gpt-4o", new ChatResult("hello", "gpt-4o", 12, 3, FromCache: false));

        Assert.True(cache.TryGetChat("c1", out var got));
        Assert.Equal("hello", got!.Content);
        Assert.Equal("gpt-4o", got.Model);
        Assert.Equal(12, got.PromptTokens);
        Assert.Equal(3, got.CompletionTokens);
        Assert.True(got.FromCache);   // served from cache
    }

    [Fact]
    public void Persists_across_reopen()
    {
        using var temp = new TempCacheDatabase();
        new SqliteLlmCache(temp.Db).PutEmbedding("k", "p", "m", new[] { 1f, 2f });

        // A fresh database handle to the same file (short-lived connections, so the file is closed).
        var reopened = new SqliteLlmCache(new LlmCacheDatabase(temp.Path));
        Assert.True(reopened.TryGetEmbedding("k", out var got));
        Assert.Equal(new[] { 1f, 2f }, got);
    }

    [Fact]
    public void Duplicate_put_is_ignored_not_thrown()
    {
        using var temp = new TempCacheDatabase();
        var cache = new SqliteLlmCache(temp.Db);
        cache.PutEmbedding("k", "p", "m", new[] { 1f });
        cache.PutEmbedding("k", "p", "m", new[] { 9f });   // ignored (first wins)

        Assert.True(cache.TryGetEmbedding("k", out var got));
        Assert.Equal(new[] { 1f }, got);
    }

    [Fact]
    public void EnsureSchema_is_idempotent()
    {
        using var temp = new TempCacheDatabase();
        temp.Db.EnsureSchema();   // second call is a no-op
        var cache = new SqliteLlmCache(temp.Db);
        cache.PutChat("c", "p", "m", new ChatResult("x", "m", null, null, false));
        Assert.True(cache.TryGetChat("c", out _));
    }
}
