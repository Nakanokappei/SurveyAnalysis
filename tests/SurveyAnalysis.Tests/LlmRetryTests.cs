using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// End-to-end retry behaviour through the client (no real sleeping: the delay is a no-op).
public class LlmRetryTests
{
    private const string ChatOk = """
        {"model":"gpt-4o","choices":[{"message":{"role":"assistant","content":"hi"}}]}
        """;

    private static OpenAiCompatibleClient Client(FakeHttpMessageHandler handler, LlmOptions? options = null)
        => new(
            new HttpClient(handler),
            new LlmProviderConfig("https://api.openai.com/v1", "sk-test", "gpt-4o"),
            new LlmProviderConfig("https://api.openai.com/v1", "sk-test", "text-embedding-3-small"),
            options ?? new LlmOptions(),
            new NullLlmCache(),
            delay: (_, _) => Task.CompletedTask,
            jitterSource: () => 0.0);

    private static ChatRequest Chat() => new("gpt-4o", new[] { new ChatMessage("user", "U") });

    [Fact]
    public async Task Retries_429_then_succeeds()
    {
        var handler = new FakeHttpMessageHandler((_, _, index) =>
            index < 2
                ? FakeHttpMessageHandler.Json("rate limited", HttpStatusCode.TooManyRequests)
                : FakeHttpMessageHandler.Json(ChatOk));

        var result = await Client(handler).CompleteAsync(Chat());

        Assert.Equal("hi", result.Content);
        Assert.Equal(3, handler.CallCount);   // 429, 429, 200
    }

    [Fact]
    public async Task Does_not_retry_client_error()
    {
        var handler = FakeHttpMessageHandler.Always("bad request", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<LlmHttpException>(() => Client(handler).CompleteAsync(Chat()));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, handler.CallCount);   // no retry on 4xx
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var handler = FakeHttpMessageHandler.Always("rate limited", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<LlmHttpException>(
            () => Client(handler, new LlmOptions { MaxRetries = 3 }).CompleteAsync(Chat()));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(3, handler.CallCount);   // 3 attempts total, then throw
    }

    [Fact]
    public async Task Retries_after_server_error()
    {
        var handler = new FakeHttpMessageHandler((_, _, index) =>
            index == 0
                ? FakeHttpMessageHandler.Json("boom", HttpStatusCode.InternalServerError)
                : FakeHttpMessageHandler.Json(ChatOk));

        var result = await Client(handler).CompleteAsync(Chat());
        Assert.Equal("hi", result.Content);
        Assert.Equal(2, handler.CallCount);
    }
}
