using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Llm.Consumers;

// Scores the sentiment of a 自由記述 text with the chat model: a polarity in [-1, +1] and a negative
// flag. JSON-mode output is forced for robust parsing; an unparseable / out-of-range reply falls back to
// neutral (0, not negative). The LLM cache dedups identical texts, so repeated answers cost one call.
public sealed class SentimentAnalyzer
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public SentimentAnalyzer(IChatClient chat, string model)
    {
        _chat = chat;
        _model = model;
    }

    public async Task<(double Score, bool IsNegative)> AnalyzeAsync(string text, CancellationToken ct = default)
    {
        var request = new ChatRequest(
            _model,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", text),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _chat.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result.Content);
    }

    private const string SystemPrompt =
        "次の日本語の文章の感情極性を判定してください。" +
        "出力は {\"score\": 数値, \"negative\": 真偽} のJSONのみ。" +
        "score は -1.0（とても否定的）〜 +1.0（とても肯定的）の小数。negative は否定的なら true。";

    private static (double Score, bool IsNegative) Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (0, false);

            var score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                ? Math.Clamp(s.GetDouble(), -1.0, 1.0)
                : 0.0;
            var negative = root.TryGetProperty("negative", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? n.GetBoolean()
                : score < 0;
            return (score, negative);
        }
        catch (JsonException)
        {
            return (0, false);   // unparseable → neutral
        }
    }
}
