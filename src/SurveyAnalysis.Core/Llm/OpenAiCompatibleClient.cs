using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm.Cache;
using SurveyAnalysis.Llm.Wire;

namespace SurveyAnalysis.Llm;

// OpenAI-compatible client (OpenAI cloud, Azure-compatible servers, and local LM Studio) over a
// shared HttpClient. Chat and embeddings each have their own provider config and their own pacing
// gate. Every request flows through: cache lookup → concurrency+pace gate → send → update gate from
// headers → retry on 429/5xx (Retry-After honored, else exponential backoff with jitter) → cache.
// Embedding inputs are batched and the batches run in parallel (bounded by the gate's semaphore).
// A blank API key (LM Studio) means no Authorization header is sent.
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmProviderConfig _chat;
    private readonly LlmProviderConfig _embedding;
    private readonly LlmOptions _options;
    private readonly ILlmCache _cache;
    private readonly RetryPolicy _retry;
    private readonly RateLimitGate _chatGate;
    private readonly RateLimitGate _embeddingGate;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenAiCompatibleClient(
        HttpClient http,
        LlmProviderConfig chat,
        LlmProviderConfig embedding,
        LlmOptions options,
        ILlmCache cache,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitterSource = null)
    {
        _http = http;
        _chat = chat;
        _embedding = embedding;
        _options = options;
        _cache = cache;
        _retry = new RetryPolicy(options.MaxRetries, options.BaseBackoff, options.MaxBackoff, jitterSource);
        _chatGate = new RateLimitGate(options.MaxConcurrency);
        _embeddingGate = new RateLimitGate(options.MaxConcurrency);
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    // ===== Chat =====

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var key = HashKey.ForChat(_chat.NormalizedEndpoint, request.Model, request.Messages,
            request.Temperature, request.MaxTokens, request.ResponseFormat, request.Seed);

        if (_options.CacheEnabled && _cache.TryGetChat(key, out var cached) && cached is not null)
            return cached;

        var wire = new ChatRequestWire
        {
            Model = request.Model,
            Messages = request.Messages.Select(m => new ChatMessageWire { Role = m.Role, Content = BuildContent(m) }).ToList(),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat is null ? null : new ResponseFormatWire { Type = request.ResponseFormat },
            Seed = request.Seed,
            Stream = false,
        };
        var json = JsonSerializer.Serialize(wire, OpenAiJsonContext.Default.ChatRequestWire);

        var body = await SendAsync(_chat, _chatGate, () => Post(_chat, "/chat/completions", json), ct).ConfigureAwait(false);
        var response = Deserialize(body, OpenAiJsonContext.Default.ChatResponseWire, "chat");

        var choice = response.Choices is { Count: > 0 } ? response.Choices[0] : null;
        var content = choice?.Message?.Content;
        if (content is null)
        {
            // Explain the empty reply instead of a bare "no content": a model refusal (its own text) or a
            // non-stop finish reason (length / content_filter) is almost always the real cause. A distinct
            // exception type lets callers fall back (e.g. OCR retries without the refused fields).
            var reason = choice?.Message?.Refusal is { Length: > 0 } refusal ? $"モデルが応答を拒否しました: {refusal}"
                : choice?.FinishReason is { Length: > 0 } finish ? $"finish_reason={finish}"
                : "応答に内容がありませんでした";
            throw new LlmEmptyResponseException($"Chat response from {_chat.Label} had no message content（{reason}）.", reason);
        }

        var result = new ChatResult(content, response.Model ?? request.Model,
            response.Usage?.PromptTokens, response.Usage?.CompletionTokens, FromCache: false);

        if (_options.CacheEnabled)
            _cache.PutChat(key, _chat.Label, request.Model, result);
        return result;
    }

    // Shapes one message's wire content: a plain string for a text-only message (backward-compatible),
    // or a multimodal parts array (the text first, then one image_url part per data URL) when the
    // message carries images for a vision request.
    private static object BuildContent(ChatMessage message)
    {
        if (message.ImageDataUrls is not { Count: > 0 } images)
            return message.Content;

        var parts = new List<ChatContentPartWire>(images.Count + 1)
        {
            new() { Type = "text", Text = message.Content },
        };
        foreach (var url in images)
            parts.Add(new ChatContentPartWire { Type = "image_url", ImageUrl = new ChatImageUrlWire { Url = url } });
        return parts;
    }

    // ===== Embeddings =====

    public async Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var results = new EmbeddingVector?[inputs.Count];
        var misses = new List<int>();

        // Serve cache hits; collect the misses (with their original positions) for fetching.
        for (var i = 0; i < inputs.Count; i++)
        {
            var key = HashKey.ForEmbedding(_embedding.NormalizedEndpoint, _embedding.DefaultModel, inputs[i]);
            if (_options.CacheEnabled && _cache.TryGetEmbedding(key, out var vector) && vector is not null)
                results[i] = new EmbeddingVector(vector, FromCache: true);
            else
                misses.Add(i);
        }

        if (misses.Count > 0)
        {
            var batches = new List<List<int>>();
            for (var start = 0; start < misses.Count; start += _options.EmbeddingBatchSize)
                batches.Add(misses.GetRange(start, Math.Min(_options.EmbeddingBatchSize, misses.Count - start)));

            var fetched = await Task.WhenAll(batches.Select(batch => FetchEmbeddingBatchAsync(batch, inputs, ct))).ConfigureAwait(false);

            for (var b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                var vectors = fetched[b];
                for (var j = 0; j < batch.Count; j++)
                {
                    var index = batch[j];
                    results[index] = new EmbeddingVector(vectors[j], FromCache: false);
                    if (_options.CacheEnabled)
                    {
                        var key = HashKey.ForEmbedding(_embedding.NormalizedEndpoint, _embedding.DefaultModel, inputs[index]);
                        _cache.PutEmbedding(key, _embedding.Label, _embedding.DefaultModel, vectors[j]);
                    }
                }
            }
        }

        var ordered = new EmbeddingVector[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
            ordered[i] = results[i] ?? throw new LlmException($"Embedding missing for input {i}.");
        return ordered;
    }

    private async Task<float[][]> FetchEmbeddingBatchAsync(List<int> batch, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var wire = new EmbeddingRequestWire
        {
            Model = _embedding.DefaultModel,
            Input = batch.Select(i => inputs[i]).ToList(),
        };
        var json = JsonSerializer.Serialize(wire, OpenAiJsonContext.Default.EmbeddingRequestWire);

        var body = await SendAsync(_embedding, _embeddingGate, () => Post(_embedding, "/embeddings", json), ct).ConfigureAwait(false);
        var response = Deserialize(body, OpenAiJsonContext.Default.EmbeddingResponseWire, "embeddings");
        if (response.Data is null)
            throw new LlmException($"Embeddings response from {_embedding.Label} had no data.");

        // The server may return data out of order; sort by index so positions line up with the batch.
        var vectors = response.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding ?? throw new LlmException("Embeddings response had a null vector."))
            .ToArray();
        if (vectors.Length != batch.Count)
            throw new LlmException($"Embeddings response returned {vectors.Length} vectors for {batch.Count} inputs.");
        return vectors;
    }

    // ===== Transport with pacing + retry =====

    private async Task<string> SendAsync(LlmProviderConfig provider, RateLimitGate gate, Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            await gate.WaitTurnAsync(ct).ConfigureAwait(false);

            var status = 0;
            var body = "";
            TimeSpan? retryAfter = null;
            Exception? transport = null;
            try
            {
                using var request = requestFactory();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                gate.Apply(RateLimitHeaders.Parse(response.Headers), now);
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return body;

                status = (int)response.StatusCode;
                retryAfter = RateLimitHeaders.RetryAfter(response, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the caller cancelled — propagate, do not retry
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Transport failure or per-request timeout: retryable (status 0).
                status = 0;
                body = ex.Message;
                transport = ex;
            }
            finally
            {
                gate.Release();
            }

            var decision = _retry.Decide(attempt, status, retryAfter);
            if (!decision.ShouldRetry)
            {
                if (status == 0)
                    throw new LlmException($"LLM request to {provider.Label} failed: {body}", transport);
                throw new LlmHttpException(status, provider.Label, body, transport);
            }

            // Push the gate forward so sibling workers also back off, then wait.
            gate.PushBack(decision.Delay, DateTimeOffset.UtcNow);
            await _delay(decision.Delay, ct).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage Post(LlmProviderConfig provider, string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, provider.NormalizedEndpoint + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // LM Studio (or any keyless server): omit Authorization entirely rather than sending "Bearer ".
        if (!string.IsNullOrEmpty(provider.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        return request;
    }

    private static T Deserialize<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string kind)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo)
                   ?? throw new LlmException($"Empty {kind} response.");
        }
        catch (JsonException ex)
        {
            throw new LlmException($"Malformed {kind} response: {ex.Message}", ex);
        }
    }
}
