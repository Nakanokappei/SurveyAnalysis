using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// The client serves repeated requests from the persistent cache without re-hitting the network, and
// only sends the cache misses when embedding a partially-cached set.
public class LlmCacheIntegrationTests
{
    private const string ChatOk = """
        {"model":"gpt-4o","choices":[{"message":{"role":"assistant","content":"hi"}}]}
        """;

    private static readonly LlmProviderConfig Chat = new("https://api.openai.com/v1", "sk-test", "gpt-4o");
    private static readonly LlmProviderConfig Embed = new("https://api.openai.com/v1", "sk-test", "text-embedding-3-small");

    private static OpenAiCompatibleClient Client(FakeHttpMessageHandler handler, ILlmCache cache)
        => new(new HttpClient(handler), Chat, Embed, new LlmOptions(), cache,
            delay: (_, _) => Task.CompletedTask, jitterSource: () => 0.0);

    [Fact]
    public async Task Repeated_chat_is_served_from_cache()
    {
        using var temp = new TempCacheDatabase();
        var cache = new SqliteLlmCache(temp.Db);
        var handler = FakeHttpMessageHandler.Always(ChatOk);
        var client = Client(handler, cache);
        var request = new ChatRequest("gpt-4o", new[] { new ChatMessage("user", "U") });

        var first = await client.CompleteAsync(request);
        var second = await client.CompleteAsync(request);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal("hi", second.Content);
        Assert.Equal(1, handler.CallCount);   // second call never hit the network
    }

    [Fact]
    public async Task Embedding_only_sends_the_cache_misses()
    {
        using var temp = new TempCacheDatabase();
        var cache = new SqliteLlmCache(temp.Db);

        // Pre-seed the cache for "b" only.
        cache.PutEmbedding(
            HashKey.ForEmbedding(Embed.NormalizedEndpoint, Embed.DefaultModel, "b"),
            Embed.Label, Embed.DefaultModel, new[] { 9f });

        var handler = new FakeHttpMessageHandler((_, body, _) =>
        {
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            var data = inputs.Select((s, i) => $$"""{"index":{{i}},"embedding":[{{(s == "a" ? 1 : s == "c" ? 3 : 0)}}]}""");
            return FakeHttpMessageHandler.Json("{\"data\":[" + string.Join(",", data) + "]}");
        });

        var vectors = await Client(handler, cache).EmbedAsync(new[] { "a", "b", "c" });

        // Only the two misses were requested, in input order.
        Assert.Equal(1, handler.CallCount);
        using var sent = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal(new[] { "a", "c" }, sent.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()).ToArray());

        Assert.False(vectors[0].FromCache);   // a — fetched
        Assert.Equal(1f, vectors[0].Values[0]);
        Assert.True(vectors[1].FromCache);    // b — from cache
        Assert.Equal(new[] { 9f }, vectors[1].Values);
        Assert.False(vectors[2].FromCache);   // c — fetched
        Assert.Equal(3f, vectors[2].Values[0]);
    }
}
