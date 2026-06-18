using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Llm.Consumers;

// Maps CSV columns to existing project fields from the column names + a few sample values. An assist layer
// over the exact-name auto-mapping the import dialog does on load: only the columns that could not be
// matched by name are sent, and the model picks the best-fitting field for each (or none). Output is
// forced to JSON; an unparseable / unknown reply yields no suggestion for that column. Used by the manual
// import dialog when an API key is configured; the 1:1 (and 選択肢-may-collect-many) rule is applied by the
// caller when these suggestions are merged in.
public sealed class LlmColumnMapper
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public LlmColumnMapper(IChatClient chat, string model)
    {
        _chat = chat;
        _model = model;
    }

    public async Task<IReadOnlyDictionary<string, string>> MapAsync(
        IReadOnlyList<(string Column, IReadOnlyList<string> Samples)> columns,
        IReadOnlyList<(string Field, string TypeLabel)> fields,
        CancellationToken ct = default)
    {
        if (columns.Count == 0 || fields.Count == 0)
            return new Dictionary<string, string>();

        var request = new ChatRequest(
            _model,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", BuildUserPrompt(columns, fields)),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _chat.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result.Content, columns, fields);
    }

    private const string SystemPrompt =
        "あなたはCSVの列を、既存のアンケート項目に対応づけるアシスタントです。" +
        "各CSV列を、意味が最もよく一致する項目に対応づけてください。明確に対応する項目がなければ null。" +
        "出力は {\"CSV列名\": \"項目名\" または null, ...} の形のJSONのみ。項目名は与えた候補の中から正確に選ぶこと。";

    private static string BuildUserPrompt(
        IReadOnlyList<(string Column, IReadOnlyList<string> Samples)> columns,
        IReadOnlyList<(string Field, string TypeLabel)> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("対応先の項目（名前：型）:");
        foreach (var (field, type) in fields)
            sb.Append("- ").Append(field).Append('：').AppendLine(type);
        sb.AppendLine();
        sb.AppendLine("対応づけるCSV列とサンプル値:");
        foreach (var (column, samples) in columns)
            sb.Append("- ").Append(column).Append(": ").AppendLine(string.Join(" | ", samples));
        return sb.ToString();
    }

    // Keeps only entries that name a real CSV column and a real target field (the model is constrained to
    // the candidate field names, but the reply is validated rather than trusted).
    private static IReadOnlyDictionary<string, string> Parse(
        string json,
        IReadOnlyList<(string Column, IReadOnlyList<string> Samples)> columns,
        IReadOnlyList<(string Field, string TypeLabel)> fields)
    {
        var result = new Dictionary<string, string>();
        var columnNames = columns.Select(c => c.Column).ToHashSet();
        var fieldNames = fields.Select(f => f.Field).ToHashSet();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String
                    && columnNames.Contains(property.Name)
                    && fieldNames.Contains(property.Value.GetString()!))
                    result[property.Name] = property.Value.GetString()!;
        }
        catch (JsonException)
        {
            // Unparseable reply → no suggestions.
        }
        return result;
    }
}
