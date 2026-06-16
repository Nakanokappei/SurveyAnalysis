using System;
using SurveyAnalysis.Llm;
using Xunit;

namespace SurveyAnalysis.Tests;

public class RateLimitGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1s", 1.0)]
    [InlineData("6m0s", 360.0)]
    [InlineData("13.2s", 13.2)]
    [InlineData("500ms", 0.5)]
    [InlineData("1h2m3s", 3723.0)]
    [InlineData("5", 5.0)]          // bare number = seconds
    public void Parses_reset_durations(string value, double expectedSeconds)
    {
        var parsed = RateLimitHeaders.ParseResetDuration(value);
        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds, parsed!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Unparseable_reset_is_null(string? value)
    {
        Assert.Null(RateLimitHeaders.ParseResetDuration(value));
    }

    [Fact]
    public void Pace_spreads_remaining_budget_over_reset_window()
    {
        var gate = new RateLimitGate(maxConcurrency: 4, lowWaterMark: 3);
        gate.Apply(new RateLimitSnapshot(RemainingRequests: 3, RemainingTokens: null,
            ResetRequests: TimeSpan.FromSeconds(30), ResetTokens: null), Now);
        Assert.Equal(Now + TimeSpan.FromSeconds(10), gate.NotBefore);   // 30s / 3 = 10s per request
    }

    [Fact]
    public void No_pace_when_budget_is_comfortable_or_headers_absent()
    {
        var comfortable = new RateLimitGate(4, lowWaterMark: 3);
        comfortable.Apply(new RateLimitSnapshot(50, null, TimeSpan.FromSeconds(30), null), Now);
        Assert.Equal(DateTimeOffset.MinValue, comfortable.NotBefore);

        var noHeaders = new RateLimitGate(4, lowWaterMark: 3);
        noHeaders.Apply(new RateLimitSnapshot(null, null, null, null), Now);   // LM Studio
        Assert.Equal(DateTimeOffset.MinValue, noHeaders.NotBefore);
    }

    [Fact]
    public void Gate_only_moves_forward()
    {
        var gate = new RateLimitGate(4, lowWaterMark: 3);
        gate.Apply(new RateLimitSnapshot(1, null, TimeSpan.FromSeconds(30), null), Now);   // +30s
        var afterFirst = gate.NotBefore;
        gate.Apply(new RateLimitSnapshot(1, null, TimeSpan.FromSeconds(1), null), Now);    // +1s (earlier)
        Assert.Equal(afterFirst, gate.NotBefore);   // unchanged — only advances
    }

    [Fact]
    public void PushBack_moves_the_gate_forward()
    {
        var gate = new RateLimitGate(4);
        gate.PushBack(TimeSpan.FromSeconds(5), Now);
        Assert.Equal(Now + TimeSpan.FromSeconds(5), gate.NotBefore);
    }
}
