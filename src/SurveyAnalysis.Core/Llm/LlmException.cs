using System;

namespace SurveyAnalysis.Llm;

// Base for all failures surfaced by the LLM layer (parse errors, exhausted retries, HTTP errors).
public class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

// A 200 response whose assistant message carried no content — usually a model refusal (its text in
// Reason) or a non-stop finish reason (length / content_filter). Distinct from the base so a caller can
// react (e.g. retry without the fields that triggered a refusal) rather than treat it as a hard error.
public sealed class LlmEmptyResponseException : LlmException
{
    public string Reason { get; }

    public LlmEmptyResponseException(string message, string reason) : base(message) => Reason = reason;
}

// A non-success HTTP response that the layer gave up on (a non-retryable 4xx, or a 429/5xx that
// outlived the retry budget). Carries the status, the provider label, and a short body snippet so
// configuration mistakes (401 bad key, 404 unknown model) surface clearly.
public sealed class LlmHttpException : LlmException
{
    public int StatusCode { get; }
    public string Provider { get; }
    public string? Body { get; }

    public LlmHttpException(int statusCode, string provider, string? body, Exception? inner = null)
        : base(Describe(statusCode, provider, body), inner)
    {
        StatusCode = statusCode;
        Provider = provider;
        Body = body;
    }

    private static string Describe(int statusCode, string provider, string? body)
    {
        var snippet = string.IsNullOrEmpty(body)
            ? ""
            : ": " + (body!.Length <= 300 ? body : body[..300] + "…");
        return $"LLM request to {provider} failed with HTTP {statusCode}{snippet}";
    }
}
