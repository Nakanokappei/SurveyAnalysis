using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SurveyAnalysis.Llm;

// Parses OpenAI's rate-limit response headers into a RateLimitSnapshot, and Retry-After into a
// delay. Tolerant of missing/blank headers (local servers) — those become null.
public static class RateLimitHeaders
{
    public static RateLimitSnapshot Parse(HttpResponseHeaders headers)
        => new(
            ParseInt(Get(headers, "x-ratelimit-remaining-requests")),
            ParseInt(Get(headers, "x-ratelimit-remaining-tokens")),
            ParseResetDuration(Get(headers, "x-ratelimit-reset-requests")),
            ParseResetDuration(Get(headers, "x-ratelimit-reset-tokens")));

    // Retry-After as a delay: either delta-seconds or an HTTP-date (converted relative to now).
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var value = response.Headers.RetryAfter;
        if (value is null)
            return null;
        if (value.Delta is { } delta)
            return delta;
        if (value.Date is { } date)
        {
            var diff = date - now;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private static string? Get(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    // OpenAI reset durations: "1s", "6m0s", "13.2s", "500ms", "1h2m3s", or a plain number (seconds).
    // Blank or unparseable -> null.
    public static TimeSpan? ParseResetDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var s = value.Trim();

        // A bare number means seconds.
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var plainSeconds))
            return TimeSpan.FromSeconds(plainSeconds);

        double totalSeconds = 0;
        var i = 0;
        var any = false;
        while (i < s.Length)
        {
            var numberStart = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                i++;
            if (i == numberStart)
                return null;
            if (!double.TryParse(s[numberStart..i], NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                return null;

            var unitStart = i;
            while (i < s.Length && char.IsLetter(s[i]))
                i++;
            var seconds = s[unitStart..i] switch
            {
                "ms" => number / 1000.0,
                "s" => number,
                "m" => number * 60.0,
                "h" => number * 3600.0,
                _ => double.NaN,
            };
            if (double.IsNaN(seconds))
                return null;
            totalSeconds += seconds;
            any = true;
        }
        return any ? TimeSpan.FromSeconds(totalSeconds) : null;
    }
}
