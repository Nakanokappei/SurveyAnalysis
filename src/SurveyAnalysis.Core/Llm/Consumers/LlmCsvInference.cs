using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Llm.Consumers;

// What the chat model infers from a CSV's columns + sample rows: a short project description and a
// per-column data type. Types correct CsvProjectImport's heuristic; the description seeds the dialog's
// 説明 box. Columns the model omits keep their heuristic type; an empty description means "no suggestion".
public sealed record CsvInference(string Description, IReadOnlyDictionary<string, FieldType> Types);

// Infers a CSV's project description + column data types in one chat call, using the column names and a
// sample of rows (and any existing description) as hints. A correction layer over the heuristic guesser:
// the caller keeps its heuristic type for any column the model omits or types as unknown, and skips this
// entirely when no API key is configured. Output is forced to JSON; an unparseable reply yields no
// overrides and no description (the heuristic stands).
public sealed class LlmCsvInference
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public LlmCsvInference(IChatClient chat, string model)
    {
        _chat = chat;
        _model = model;
    }

    // The allowed type labels, mapped back to FieldType. Uses the same Japanese labels the UI shows.
    private static readonly IReadOnlyDictionary<string, FieldType> ByLabel =
        Enum.GetValues<FieldType>().ToDictionary(FieldTypeInfo.Label);

    public async Task<CsvInference> InferAsync(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string existingDescription,
        CancellationToken ct = default)
    {
        if (headers.Count == 0)
            return new CsvInference("", new Dictionary<string, FieldType>());

        var request = new ChatRequest(
            _model,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", BuildUserPrompt(headers, rows, existingDescription)),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _chat.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result.Content, headers);
    }

    private const string SystemPrompt =
        "あなたはアンケートCSVを解析するアシスタントです。列名とサンプル値から、(1)このアンケートが何についてのものかの短い説明（1〜2文、日本語）と、(2)各列のデータ型を判定してください。" +
        "データ型は次のいずれか：氏名, 性別, 住所, 電話番号, メールアドレス, 日付, 選択肢, 数値, テキスト, テキスト（改行あり）。" +
        "「選択肢」は決まった選択肢から選ぶ列（「; 」区切りの複数選択を含む）。「テキスト（改行あり）」は自由記述の長文・感想。「テキスト」はそれ以外の短い語句。" +
        "出力は次の形のJSONのみ：{\"description\": \"...\", \"types\": {\"列名\": \"型ラベル\", ...}}。説明文以外は出力しないこと。";

    private static string BuildUserPrompt(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, string existingDescription)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingDescription))
            sb.Append("現在の説明（参考）: ").AppendLine(existingDescription.Trim()).AppendLine();

        sb.AppendLine("列とサンプル値:");
        for (var c = 0; c < headers.Count; c++)
        {
            var samples = rows
                .Select(r => c < r.Count ? r[c] : "")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .Take(5);
            sb.Append("- ").Append(headers[c]).Append(": ").AppendLine(string.Join(" | ", samples));
        }
        return sb.ToString();
    }

    private static CsvInference Parse(string json, IReadOnlyList<string> headers)
    {
        var types = new Dictionary<string, FieldType>();
        var description = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new CsvInference("", types);

            if (root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                description = desc.GetString()!.Trim();

            // Prefer a "types" object; otherwise treat the root itself as the flat column→label map.
            var typeMap = root.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Object ? t : root;
            foreach (var header in headers)
                if (typeMap.TryGetProperty(header, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && ByLabel.TryGetValue(value.GetString()!.Trim(), out var type))
                    types[header] = type;
        }
        catch (JsonException)
        {
            // Unparseable model output: keep the heuristic types and no description.
        }
        return new CsvInference(description, types);
    }
}
