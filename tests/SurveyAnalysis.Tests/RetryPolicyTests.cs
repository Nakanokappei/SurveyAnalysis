using System;
using SurveyAnalysis.Llm;
using Xunit;

namespace SurveyAnalysis.Tests;

public class RetryPolicyTests
{
    private static RetryPolicy Policy(int maxAttempts = 5, double jitter = 1.0)
        => new(maxAttempts, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(60), () => jitter);

    [Fact]
    public void Honors_retry_after_exactly()
    {
        var d = Policy().Decide(attempt: 1, statusCode: 429, retryAfter: TimeSpan.FromSeconds(2));
        Assert.True(d.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(2), d.Delay);
    }

    [Fact]
    public void Exponential_backoff_with_injected_jitter()
    {
        var p = new RetryPolicy(5, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(60), () => 0.5);
        Assert.Equal(TimeSpan.FromMilliseconds(500), p.Decide(1, 429, null).Delay);   // 1000*2^0*0.5
        Assert.Equal(TimeSpan.FromMilliseconds(1000), p.Decide(2, 429, null).Delay);  // 1000*2^1*0.5
        Assert.Equal(TimeSpan.FromMilliseconds(2000), p.Decide(3, 429, null).Delay);  // 1000*2^2*0.5
    }

    [Fact]
    public void Backoff_is_capped_at_max()
    {
        var p = new RetryPolicy(10, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1500), () => 1.0);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), p.Decide(5, 429, null).Delay);  // 1000*16 capped to 1500
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(0, true)]      // transport error
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Retryable_status_codes(int status, bool retryable)
    {
        Assert.Equal(retryable, Policy().Decide(1, status, null).ShouldRetry);
    }

    [Fact]
    public void Gives_up_once_max_attempts_reached()
    {
        var p = Policy(maxAttempts: 3);
        Assert.True(p.Decide(1, 429, null).ShouldRetry);
        Assert.True(p.Decide(2, 429, null).ShouldRetry);
        Assert.False(p.Decide(3, 429, null).ShouldRetry);   // 3rd attempt failed, no 4th
    }
}
