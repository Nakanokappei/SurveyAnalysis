using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// Request shaping and response parsing for the OpenAI-compatible client.
public class LlmClientTests
{
    private const string ChatOk = """
        {"model":"gpt-4o","choices":[{"message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}
        """;

    private static OpenAiCompatibleClient Client(
        FakeHttpMessageHandler handler,
        ILlmCache? cache = null,
        LlmOptions? options = null,
        string chatEndpoint = "https://api.openai.com/v1",
        string chatKey = "sk-test",
        string embeddingEndpoint = "http://localhost:1234/v1",
        string embeddingKey = "")
        => new(
            new HttpClient(handler),
            new LlmProviderConfig(chatEndpoint, chatKey, "gpt-4o"),
            new LlmProviderConfig(embeddingEndpoint, embeddingKey, "text-embedding-3-small"),
            options ?? new LlmOptions(),
            cache ?? new NullLlmCache(),
            delay: (_, _) => Task.CompletedTask,
            jitterSource: () => 0.0);

    private static ChatRequest Chat() => new("gpt-4o",
        new[] { new ChatMessage("system", "S"), new ChatMessage("user", "U") },
        Temperature: 0.2, ResponseFormat: "json_object");

    [Fact]
    public async Task Chat_request_is_shaped_correctly()
    {
        var handler = FakeHttpMessageHandler.Always(ChatOk);
        await Client(handler).CompleteAsync(Chat());

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", req.Url);
        Assert.Equal("Bearer sk-test", req.Authorization);

        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("max_tokens", out _));   // null field omitted
        Assert.Equal(2, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Blank_api_key_omits_authorization_header()
    {
        var handler = FakeHttpMessageHandler.Always("""{"data":[{"index":0,"embedding":[1.0]}]}""");
        await Client(handler).EmbedAsync(new[] { "x" });

        var req = handler.Requests.Single();
        Assert.Equal("http://localhost:1234/v1/embeddings", req.Url);   // local endpoint
        Assert.Null(req.Authorization);                                  // no Bearer for LM Studio
    }

    [Fact]
    public async Task Endpoint_trailing_slash_is_normalized()
    {
        var handler = FakeHttpMessageHandler.Always(ChatOk);
        await Client(handler, chatEndpoint: "https://api.openai.com/v1/").CompleteAsync(Chat());
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task Chat_response_is_parsed_with_usage()
    {
        var result = await Client(FakeHttpMessageHandler.Always(ChatOk)).CompleteAsync(Chat());
        Assert.Equal("hi", result.Content);
        Assert.Equal("gpt-4o", result.Model);
        Assert.Equal(5, result.PromptTokens);
        Assert.Equal(2, result.CompletionTokens);
        Assert.False(result.FromCache);
    }

    [Fact]
    public async Task Embeddings_are_reordered_by_index_and_preserve_input_order()
    {
        // Server returns the two vectors out of order (index 1 before index 0).
        var handler = FakeHttpMessageHandler.Always(
            """{"data":[{"index":1,"embedding":[2.0,2.0]},{"index":0,"embedding":[1.0,1.0]}]}""");

        var vectors = await Client(handler).EmbedAsync(new[] { "first", "second" });

        Assert.Equal(new[] { 1.0f, 1.0f }, vectors[0].Values);   // input 0 -> index 0
        Assert.Equal(new[] { 2.0f, 2.0f }, vectors[1].Values);   // input 1 -> index 1
    }

    [Fact]
    public async Task Malformed_response_throws_llm_exception()
    {
        var handler = FakeHttpMessageHandler.Always("not json");
        await Assert.ThrowsAsync<LlmException>(() => Client(handler).CompleteAsync(Chat()));
    }

    [Fact]
    public async Task Text_only_message_sends_content_as_a_string()
    {
        var handler = FakeHttpMessageHandler.Always(ChatOk);
        await Client(handler).CompleteAsync(Chat());

        using var doc = JsonDocument.Parse(handler.Requests.Single().Body);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);   // backward-compatible string form
    }

    [Fact]
    public async Task Message_with_images_sends_a_multimodal_content_array()
    {
        var handler = FakeHttpMessageHandler.Always(ChatOk);
        var request = new ChatRequest("gpt-4o", new[]
        {
            new ChatMessage("system", "S"),
            new ChatMessage("user", "読み取って", new[] { "data:image/png;base64,AAAA" }),
        }, Temperature: 0, ResponseFormat: "json_object");

        await Client(handler).CompleteAsync(request);

        using var doc = JsonDocument.Parse(handler.Requests.Single().Body);
        var messages = doc.RootElement.GetProperty("messages");
        // System stays a plain string; the user message becomes a [text, image_url] array.
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind);
        var parts = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("読み取って", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }
}
