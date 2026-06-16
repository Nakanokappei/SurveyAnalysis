using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Llm;

public enum LlmConnectionResult
{
    Ok,            // reachable and authorized
    Unauthorized,  // reachable but the key was rejected (401/403)
    Unreachable,   // network error, timeout, or any other non-success status
}

// Verifies an OpenAI-compatible endpoint + key cheaply: GET {endpoint}/models lists models, which
// costs no tokens (not billed), yet still requires a valid key on a cloud provider. 200 => Ok;
// 401/403 => Unauthorized (bad key); anything else or a transport failure => Unreachable. A blank key
// omits the Authorization header (local servers such as LM Studio need no auth).
public static class LlmConnectionTester
{
    public static async Task<LlmConnectionResult> VerifyAsync(string endpoint, string apiKey, HttpClient http, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LlmProviderConfig.Normalize(endpoint) + "/models");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return LlmConnectionResult.Ok;
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? LlmConnectionResult.Unauthorized
                : LlmConnectionResult.Unreachable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // caller cancelled (e.g. a newer check superseded this one)
        }
        catch
        {
            return LlmConnectionResult.Unreachable;   // DNS / connection / timeout / TLS, etc.
        }
    }
}
