using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Tests;

// A scripted HttpMessageHandler for testing the LLM client with no network. The responder decides
// each reply from the request, its body, and the 0-based call index. Records every request (URL,
// Authorization, body) and tracks peak concurrency so batching/semaphore behaviour is assertable.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    internal sealed record Recorded(HttpMethod Method, string Url, string? Authorization, string Body);

    private readonly Func<HttpRequestMessage, string, int, HttpResponseMessage> _responder;
    private readonly Func<Task>? _onEnter;
    private readonly object _lock = new();
    private int _inFlight;

    public List<Recorded> Requests { get; } = new();
    public int CallCount => Requests.Count;
    public int MaxConcurrent { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder, Func<Task>? onEnter = null)
    {
        _responder = responder;
        _onEnter = onEnter;
    }

    // Convenience: always return the same body/status (+ optional headers).
    public static FakeHttpMessageHandler Always(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new((_, _, _) => Json(body, status));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        int index;
        lock (_lock)
        {
            index = Requests.Count;
            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            _inFlight++;
            if (_inFlight > MaxConcurrent)
                MaxConcurrent = _inFlight;
        }
        try
        {
            if (_onEnter is not null)
                await _onEnter();
            return _responder(request, body, index);
        }
        finally
        {
            lock (_lock) { _inFlight--; }
        }
    }
}
