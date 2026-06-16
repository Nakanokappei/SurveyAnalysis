using System;
using System.Net.Http;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// Manual smoke tests against real servers — always Skipped so CI never makes network calls or needs
// keys. To run one: remove its Skip locally (or run by name) after setting the env var / starting the
// local server. Keys are read from the environment, never hard-coded or committed.
public class LlmSmokeTests
{
    [Fact(Skip = "manual: set OPENAI_API_KEY, then run explicitly")]
    public async Task OpenAI_chat_and_embedding()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        var client = new OpenAiCompatibleClient(
            new HttpClient(),
            new LlmProviderConfig("https://api.openai.com/v1", key, "(chat)"),
            new LlmProviderConfig("https://api.openai.com/v1", key, "text-embedding-3-small"),
            new LlmOptions(),
            new NullLlmCache());

        var chat = await client.CompleteAsync(new ChatRequest("gpt-4o-mini",
            new[] { new ChatMessage("user", "Reply with the single word: ok") }));
        Assert.False(string.IsNullOrWhiteSpace(chat.Content));

        var vectors = await client.EmbedAsync(new[] { "hello", "world" });
        Assert.Equal(2, vectors.Count);
        Assert.True(vectors[0].Values.Length > 100);   // a real embedding has many dimensions
    }

    [Fact(Skip = "manual: start LM Studio local server, then run explicitly")]
    public async Task LmStudio_local_chat_and_embedding()
    {
        const string baseUrl = "http://localhost:1234/v1";
        var client = new OpenAiCompatibleClient(
            new HttpClient(),
            new LlmProviderConfig(baseUrl, "", "local-model"),   // blank key -> no Authorization header
            new LlmProviderConfig(baseUrl, "", "nomic-embed-text-v1.5"),
            new LlmOptions(),
            new NullLlmCache());

        var chat = await client.CompleteAsync(new ChatRequest("local-model",
            new[] { new ChatMessage("user", "Reply with: ok") }));
        Assert.False(string.IsNullOrWhiteSpace(chat.Content));

        var vectors = await client.EmbedAsync(new[] { "hello" });
        Assert.Single(vectors);
    }
}
