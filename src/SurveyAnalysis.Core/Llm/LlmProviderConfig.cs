using System;

namespace SurveyAnalysis.Llm;

// Identity of one OpenAI-compatible endpoint: base URL + API key + the model used by default. Chat
// and embeddings each get their own config (the user may point one at OpenAI cloud and the other at
// a local LM Studio). A blank ApiKey is valid (LM Studio) — the client then omits the Authorization
// header. The normalized endpoint (trimmed, no trailing slash) is part of every cache key.
public sealed record LlmProviderConfig(string Endpoint, string ApiKey, string DefaultModel)
{
    public string NormalizedEndpoint => Normalize(Endpoint);

    // The base URL with surrounding whitespace and any trailing slash removed, so that
    // "http://localhost:1234/v1" and ".../v1/" produce identical URLs and cache keys.
    public static string Normalize(string? endpoint) => (endpoint ?? "").Trim().TrimEnd('/');

    // Stable provider label for cache auditing and error messages (endpoint + model).
    public string Label => $"{NormalizedEndpoint}|{DefaultModel}";
}

// Tuning knobs shared by both providers. Defaults are conservative so a small cloud quota does not
// get hammered; LM Studio users can raise MaxConcurrency freely.
public sealed record LlmOptions
{
    public int MaxConcurrency { get; init; } = 4;          // SemaphoreSlim permits per provider
    public int EmbeddingBatchSize { get; init; } = 64;     // inputs per /embeddings request
    public int MaxRetries { get; init; } = 5;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public bool CacheEnabled { get; init; } = true;
}
