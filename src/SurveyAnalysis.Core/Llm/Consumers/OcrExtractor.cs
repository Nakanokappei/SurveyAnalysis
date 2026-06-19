using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Llm.Consumers;

// Reads one scanned survey-form image into field values with a vision chat model (the OcrModel). The
// project's fields (name + data type) and its 説明 are passed as hints so the model knows which values
// to look for and what shape each takes; output is forced to JSON (項目名→値). Unreadable or blank
// fields are omitted. The LLM cache keys on the image bytes, so re-scanning the same image is free.
public sealed class OcrExtractor
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public OcrExtractor(IChatClient chat, string model)
    {
        _chat = chat;
        _model = model;
    }

    // Extracts a 項目名→値 map from the image. Returns an empty map when there are no fields / no image
    // bytes or the reply is unparseable; callers treat an empty map as "nothing read". choiceOptions maps
    // a 選択肢 field's name to its known options (gathered from existing answers): listing them in the
    // prompt sharply improves which ticked boxes the model reports. A field with no entry is unconstrained.
    public async Task<IReadOnlyDictionary<string, string>> ExtractAsync(
        byte[] imageBytes,
        string mediaType,
        IReadOnlyList<DataField> fields,
        string projectDescription,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? choiceOptions = null,
        CancellationToken ct = default)
    {
        if (fields.Count == 0 || imageBytes.Length == 0)
            return new Dictionary<string, string>();

        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}";
        var request = new ChatRequest(
            _model,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", BuildUserPrompt(fields, projectDescription, choiceOptions), new[] { dataUrl }),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _chat.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result.Content, fields);
    }

    // Turns an extracted 項目名→値 map into one response: an answer per field that has a non-empty value,
    // keyed by the field name (the same shape CsvProjectImport.BuildResponses produces, so it flows into
    // ResponseRepository / the import analysis identically).
    public static SurveyResponse BuildResponse(IReadOnlyDictionary<string, string> values, IReadOnlyList<DataField> fields)
    {
        var answers = new List<FieldAnswer>();
        foreach (var field in fields)
            if (values.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                answers.Add(new FieldAnswer(field.Name, value.Trim()));
        return new SurveyResponse { Answers = answers };
    }

    private const string SystemPrompt =
        "あなたはスキャンされたアンケート帳票を読み取るアシスタントです。" +
        "画像から各項目の値を読み取り、指定された項目名をキーにしたJSONで返してください。" +
        "値は文字列。日付は YYYY/MM/DD 形式。" +
        "選択肢の項目は、提示された選択肢の中から、チェック・丸などで選ばれているものだけを選んでください。" +
        "複数該当する場合は「; 」で区切る。" +
        "読み取れない・記入されていない項目は空文字 \"\" にしてください。" +
        "出力は {\"項目名\": \"値\", ...} のJSONのみ。";

    // Lists each field as "項目名: データ型" so the model maps printed answers to the right keys, with the
    // project 説明 prepended as domain context when one is set. For a 選択肢 field with known options the
    // options are listed inline so the model picks from them rather than paraphrasing the printed labels.
    private static string BuildUserPrompt(
        IReadOnlyList<DataField> fields,
        string projectDescription,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? choiceOptions)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(projectDescription))
            sb.Append("アンケートの概要: ").AppendLine(projectDescription.Trim()).AppendLine();

        sb.AppendLine("読み取る項目（項目名: データ型）:");
        foreach (var field in fields)
        {
            sb.Append("- ").Append(field.Name).Append(": ").Append(FieldTypeInfo.Label(field.FieldType));
            // Inline the allowed options for a choice field so the model selects from the exact labels.
            if (field.FieldType == FieldType.Choice
                && choiceOptions is not null
                && choiceOptions.TryGetValue(field.Name, out var options)
                && options.Count > 0)
                sb.Append("（選択肢: ").Append(string.Join(" / ", options)).Append("）");
            sb.AppendLine();
        }
        sb.AppendLine().Append("画像を読み取り、各項目の値をJSONで返してください。");
        return sb.ToString();
    }

    // Pulls each known field's value out of the flat JSON object. A string value is taken verbatim; a
    // number / boolean is accepted as its literal text (a model may emit a 数値 field as a JSON number);
    // anything else (or a blank) is skipped, so the field simply contributes no answer.
    private static IReadOnlyDictionary<string, string> Parse(string json, IReadOnlyList<DataField> fields)
    {
        var values = new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return values;

            foreach (var field in fields)
            {
                if (!root.TryGetProperty(field.Name, out var value))
                    continue;
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                    values[field.Name] = text!.Trim();
            }
        }
        catch (JsonException)
        {
            // Unparseable model output → no values read.
        }
        return values;
    }
}
