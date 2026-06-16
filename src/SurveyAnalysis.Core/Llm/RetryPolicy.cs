using System;

namespace SurveyAnalysis.Llm;

// The outcome of a retry decision: whether to try again, and how long to wait first.
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay);

// Pure, deterministic retry logic (no clock, no I/O) so it is fully unit-testable. A server-provided
// Retry-After is honored as-is; otherwise it is exponential backoff with full jitter. Only 429 / 5xx
// and transport errors (status 0) are retryable — other 4xx (bad key, unknown model) fail fast.
public sealed class RetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly Func<double> _nextUnitInterval;   // returns [0,1); injectable for deterministic tests

    public RetryPolicy(int maxAttempts, TimeSpan baseBackoff, TimeSpan maxBackoff, Func<double>? jitterSource = null)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseBackoff = baseBackoff;
        _maxBackoff = maxBackoff;
        _nextUnitInterval = jitterSource ?? Random.Shared.NextDouble;
    }

    // Status 0 represents a transport-level failure (connection reset / timeout) and is retryable.
    public static bool IsRetryable(int statusCode) => statusCode == 0 || statusCode == 429 || statusCode >= 500;

    // attempt is the 1-based number of the attempt that just failed.
    public RetryDecision Decide(int attempt, int statusCode, TimeSpan? retryAfter)
    {
        if (attempt >= _maxAttempts || !IsRetryable(statusCode))
            return new RetryDecision(false, TimeSpan.Zero);

        // A server-provided Retry-After wins and is honored exactly (the server knows its own window).
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return new RetryDecision(true, ra);

        // Full jitter: random in [0, min(base * 2^(attempt-1), max)).
        var exponential = _baseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, _maxBackoff.TotalMilliseconds);
        var jittered = capped * _nextUnitInterval();
        return new RetryDecision(true, TimeSpan.FromMilliseconds(jittered));
    }
}
