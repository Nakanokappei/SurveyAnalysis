using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Cache;
using Xunit;

namespace SurveyAnalysis.Tests;

// Embedding batching, order preservation, and concurrency bounding.
public class LlmBatchingTests
{
    // Responder that echoes one 1-D vector per input, encoding the input's numeric value, with the
    // per-batch index so the client's reorder logic is exercised.
    private static FakeHttpMessageHandler EchoHandler(System.Func<Task>? onEnter = null)
        => new((_, body, _) =>
        {
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            var data = inputs.Select((s, i) =>
                $$"""{"index":{{i}},"embedding":[{{double.Parse(s, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}}]}""");
            return FakeHttpMessageHandler.Json("{\"data\":[" + string.Join(",", data) + "]}");
        }, onEnter);

    private static OpenAiCompatibleClient Client(FakeHttpMessageHandler handler, LlmOptions options)
        => new(
            new HttpClient(handler),
            new LlmProviderConfig("https://api.openai.com/v1", "sk-test", "gpt-4o"),
            new LlmProviderConfig("https://api.openai.com/v1", "sk-test", "text-embedding-3-small"),
            options,
            new NullLlmCache(),
            delay: (_, _) => Task.CompletedTask,
            jitterSource: () => 0.0);

    [Fact]
    public async Task Splits_into_batches_and_preserves_order()
    {
        var inputs = Enumerable.Range(0, 130).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var handler = EchoHandler();
        var client = Client(handler, new LlmOptions { EmbeddingBatchSize = 64, MaxConcurrency = 4 });

        var vectors = await client.EmbedAsync(inputs);

        Assert.Equal(130, vectors.Count);
        Assert.Equal(3, handler.CallCount);                 // ceil(130/64) = 3 requests
        for (var i = 0; i < inputs.Count; i++)
            Assert.Equal((float)i, vectors[i].Values[0]);   // each input maps back to its own vector
    }

    [Fact]
    public async Task Concurrency_is_bounded_by_the_semaphore()
    {
        var inputs = Enumerable.Range(0, 200).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var handler = EchoHandler(onEnter: () => Task.Delay(15));   // hold requests so overlap is observable
        var client = Client(handler, new LlmOptions { EmbeddingBatchSize = 10, MaxConcurrency = 1 });

        await client.EmbedAsync(inputs);

        Assert.Equal(20, handler.CallCount);     // 200 / 10
        Assert.Equal(1, handler.MaxConcurrent);  // semaphore of 1 serializes every batch
    }

    [Fact]
    public async Task Concurrency_allows_parallel_batches_up_to_the_limit()
    {
        var inputs = Enumerable.Range(0, 200).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var handler = EchoHandler(onEnter: () => Task.Delay(30));
        var client = Client(handler, new LlmOptions { EmbeddingBatchSize = 10, MaxConcurrency = 4 });

        await client.EmbedAsync(inputs);

        Assert.True(handler.MaxConcurrent > 1, $"expected parallelism, saw {handler.MaxConcurrent}");
        Assert.True(handler.MaxConcurrent <= 4, $"expected <= 4, saw {handler.MaxConcurrent}");
    }
}
