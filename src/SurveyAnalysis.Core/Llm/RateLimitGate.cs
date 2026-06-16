using System;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Llm;

// A snapshot of a provider's rate-limit budget, parsed from one response's headers (any field may be
// absent — e.g. LM Studio returns none).
public readonly record struct RateLimitSnapshot(
    int? RemainingRequests,
    int? RemainingTokens,
    TimeSpan? ResetRequests,
    TimeSpan? ResetTokens);

// Per-provider pacing. Combines a concurrency permit (SemaphoreSlim) with a proactive "not before"
// gate derived from rate-limit headers: when a budget runs low, the remaining allowance is spread
// across its reset window so we glide under the limit instead of slamming into a 429. The gate only
// ever moves forward, and all workers respect the single shared timestamp. With no headers (local
// servers) the gate never advances, so concurrency is bounded only by the semaphore.
public sealed class RateLimitGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _lowWaterMark;
    private readonly object _lock = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public RateLimitGate(int maxConcurrency, int lowWaterMark = 3)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        _lowWaterMark = Math.Max(1, lowWaterMark);
    }

    public DateTimeOffset NotBefore
    {
        get { lock (_lock) { return _notBefore; } }
    }

    // Acquire a concurrency permit, then wait until the proactive pace allows sending. Always pair
    // with Release() in a finally.
    public async Task WaitTurnAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        TimeSpan wait;
        lock (_lock) { wait = _notBefore - DateTimeOffset.UtcNow; }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    public void Release() => _semaphore.Release();

    // Update the pace from a response's headers: if a budget is at/below the low-water mark, spread
    // what remains over its reset window. Only moves the gate forward.
    public void Apply(RateLimitSnapshot snapshot, DateTimeOffset now)
    {
        var pace = ComputePace(snapshot);
        if (pace is { } p)
            PushForward(now + p);
    }

    // Push the gate forward by an explicit delay (Retry-After / backoff) so sibling workers also wait.
    public void PushBack(TimeSpan delay, DateTimeOffset now)
    {
        if (delay > TimeSpan.Zero)
            PushForward(now + delay);
    }

    private void PushForward(DateTimeOffset target)
    {
        lock (_lock) { if (target > _notBefore) _notBefore = target; }
    }

    private TimeSpan? ComputePace(RateLimitSnapshot snapshot)
    {
        TimeSpan? pace = null;
        Consider(snapshot.RemainingRequests, snapshot.ResetRequests, ref pace);
        Consider(snapshot.RemainingTokens, snapshot.ResetTokens, ref pace);
        return pace;
    }

    private void Consider(int? remaining, TimeSpan? reset, ref TimeSpan? pace)
    {
        if (remaining is { } r && reset is { } window && r <= _lowWaterMark && window > TimeSpan.Zero)
        {
            var perRequest = window / (double)Math.Max(r, 1);
            if (pace is null || perRequest > pace)
                pace = perRequest;
        }
    }
}
