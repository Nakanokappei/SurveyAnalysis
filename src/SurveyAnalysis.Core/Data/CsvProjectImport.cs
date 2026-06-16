using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Turns a parsed CSV into the pieces needed to create a project from it: one survey field (データ項目)
// per column, with a data type guessed from the column's values, and one response per data row with
// the cell values mapped back to those fields by name. Used by the "CSV からプロジェクトを作る" flow,
// where the user reviews the guessed schema in the project design dialog before the project is saved.
public static class CsvProjectImport
{
    // Builds a starter field per CSV column. The field name is the column header; the data type is
    // guessed from the column's sample values (a clean date column → 日付, a clean numeric column →
    // 数値, anything else → フリーテキスト). The first 日付 column is marked as the aggregation basis so
    // the time slices work without further editing — mirroring the manual-create default (記入日).
    public static IReadOnlyList<DataField> InferFields(CsvFile csv)
    {
        var fields = new List<DataField>();
        var aggregationAssigned = false;
        for (var col = 0; col < csv.Header.Count; col++)
        {
            var type = GuessType(ColumnValues(csv, col));
            var field = new DataField
            {
                Name = csv.Header[col],
                FieldType = type,
                Analysis = AnalysisMethod.None,
            };
            // The first date column becomes the time/monthly aggregation basis (the record date).
            if (type == FieldType.Date && !aggregationAssigned)
            {
                field.UseForAggregation = true;
                aggregationAssigned = true;
            }
            fields.Add(field);
        }
        return fields;
    }

    // Builds one response per data row. Each project field pulls its value from the CSV column whose
    // header equals the field's name. The design dialog seeds names from headers, so this is the
    // identity mapping for unchanged fields; a renamed or added field with no matching column simply
    // contributes no answer, and a dropped column is ignored.
    public static IReadOnlyList<SurveyResponse> BuildResponses(IReadOnlyList<DataField> fields, CsvFile csv)
    {
        // Resolve each field's source column once (first header match, or -1 when there is none).
        var columnForField = fields.Select(f => IndexOfHeader(csv, f.Name)).ToArray();

        var responses = new List<SurveyResponse>();
        foreach (var row in csv.Rows)
        {
            var answers = new List<FieldAnswer>();
            for (var i = 0; i < fields.Count; i++)
            {
                var col = columnForField[i];
                if (col < 0)
                    continue;
                var value = col < row.Length ? row[col] : "";
                answers.Add(new FieldAnswer(fields[i].Name, value));
            }
            if (answers.Count > 0)
                responses.Add(new SurveyResponse { Answers = answers });
        }
        return responses;
    }

    // The non-empty, trimmed values in one column — the sample used to guess that column's type.
    private static IReadOnlyList<string> ColumnValues(CsvFile csv, int col)
    {
        var values = new List<string>();
        foreach (var row in csv.Rows)
            if (col < row.Length && !string.IsNullOrWhiteSpace(row[col]))
                values.Add(row[col].Trim());
        return values;
    }

    // Guesses a column's data type from its values: 日付 if every value parses as a date, 数値 if every
    // value parses as a number, otherwise フリーテキスト. An empty column defaults to フリーテキスト. The
    // guess is only a starting point — the user confirms or changes it in the design dialog.
    private static FieldType GuessType(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return FieldType.FreeText;
        if (values.All(DateParsing.IsDate))
            return FieldType.Date;
        if (values.All(IsNumber))
            return FieldType.Number;
        return FieldType.FreeText;
    }

    private static bool IsNumber(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    // The index of the first column whose header equals the given field name, or -1 if none matches.
    private static int IndexOfHeader(CsvFile csv, string name)
    {
        for (var i = 0; i < csv.Header.Count; i++)
            if (csv.Header[i] == name)
                return i;
        return -1;
    }
}
