using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using Xunit;

namespace SurveyAnalysis.Tests;

public class LlmConnectionTesterTests
{
    [Fact]
    public async Task Ok_on_success_and_calls_models_endpoint_with_bearer()
    {
        var handler = FakeHttpMessageHandler.Always("{\"data\":[]}");
        var result = await LlmConnectionTester.VerifyAsync("https://api.openai.com/v1/", "sk-test", new HttpClient(handler));

        Assert.Equal(LlmConnectionResult.Ok, result);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.openai.com/v1/models", req.Url);   // trailing slash normalized
        Assert.Equal("Bearer sk-test", req.Authorization);
    }

    [Fact]
    public async Task Blank_key_omits_authorization()
    {
        var handler = FakeHttpMessageHandler.Always("{}");
        await LlmConnectionTester.VerifyAsync("http://localhost:1234/v1", "", new HttpClient(handler));
        Assert.Null(handler.Requests.Single().Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmConnectionResult.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, LlmConnectionResult.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, LlmConnectionResult.Unreachable)]
    [InlineData(HttpStatusCode.NotFound, LlmConnectionResult.Unreachable)]
    public async Task Maps_status_codes(HttpStatusCode status, LlmConnectionResult expected)
    {
        var handler = FakeHttpMessageHandler.Always("nope", status);
        var result = await LlmConnectionTester.VerifyAsync("https://api.openai.com/v1", "sk", new HttpClient(handler));
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Transport_failure_is_unreachable()
    {
        var handler = new FakeHttpMessageHandler((_, _, _) => throw new HttpRequestException("boom"));
        var result = await LlmConnectionTester.VerifyAsync("https://api.openai.com/v1", "sk", new HttpClient(handler));
        Assert.Equal(LlmConnectionResult.Unreachable, result);
    }
}
