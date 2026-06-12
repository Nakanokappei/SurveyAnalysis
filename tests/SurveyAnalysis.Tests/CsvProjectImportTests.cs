using System.Linq;
using System.Text;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// CsvProjectImport turns a parsed CSV into a starter schema (one field per column, type guessed from
// the data) and into responses (one per row, mapped to the fields by name). These exercise the type
// guessing, the aggregation-date defaulting, and the name-based row mapping that the "CSV からプロジェ
// クトを作る" flow relies on.
public class CsvProjectImportTests
{
    private static CsvFile Parse(string text) => CsvFile.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void InferFields_names_columns_and_guesses_types()
    {
        var csv = Parse("記入日,満足度,ご意見\n2026/05/20,5,良かった\n2026/08/03,4,普通\n");

        var fields = CsvProjectImport.InferFields(csv);

        Assert.Equal(new[] { "記入日", "満足度", "ご意見" }, fields.Select(f => f.Name));
        Assert.Equal(FieldType.Date, fields[0].FieldType);     // clean date column
        Assert.Equal(FieldType.Number, fields[1].FieldType);   // clean numeric column
        Assert.Equal(FieldType.FreeText, fields[2].FieldType); // free text
    }

    [Fact]
    public void InferFields_marks_only_the_first_date_column_for_aggregation()
    {
        var csv = Parse("受付日,完了日,件名\n2026/05/20,2026/05/25,工事A\n2026/06/01,2026/06/03,工事B\n");

        var fields = CsvProjectImport.InferFields(csv);

        Assert.Equal(FieldType.Date, fields[0].FieldType);
        Assert.Equal(FieldType.Date, fields[1].FieldType);
        Assert.True(fields[0].UseForAggregation);   // first date column is the aggregation basis
        Assert.False(fields[1].UseForAggregation);   // later date columns are not
    }

    [Fact]
    public void InferFields_treats_mixed_or_empty_columns_as_free_text()
    {
        // First column mixes a number and text; second column is entirely empty.
        var csv = Parse("値,メモ\n5,\nテキスト,\n");

        var fields = CsvProjectImport.InferFields(csv);

        Assert.Equal(FieldType.FreeText, fields[0].FieldType);
        Assert.Equal(FieldType.FreeText, fields[1].FieldType);
        Assert.False(fields[0].UseForAggregation);
    }

    [Fact]
    public void BuildResponses_maps_each_row_to_answers_by_field_name()
    {
        var csv = Parse("記入日,ご意見\n2026/05/20,良かった\n2026/08/03,普通\n");
        var fields = CsvProjectImport.InferFields(csv);

        var responses = CsvProjectImport.BuildResponses(fields, csv);

        Assert.Equal(2, responses.Count);
        Assert.Equal(new[] { "記入日", "ご意見" }, responses[0].Answers.Select(a => a.FieldName));
        Assert.Equal("良かった", responses[0].Answers.Single(a => a.FieldName == "ご意見").Value);
        Assert.Equal("2026/08/03", responses[1].Answers.Single(a => a.FieldName == "記入日").Value);
    }

    [Fact]
    public void BuildResponses_ignores_fields_with_no_matching_column()
    {
        var csv = Parse("記入日,ご意見\n2026/05/20,良かった\n");
        var fields = CsvProjectImport.InferFields(csv);
        // Simulate the user renaming a field in the design dialog: it no longer matches any header.
        fields[1].Name = "別名";

        var responses = CsvProjectImport.BuildResponses(fields, csv);

        Assert.Single(responses);
        // Only the still-matching 記入日 column contributes; the renamed field has no source column.
        Assert.Equal(new[] { "記入日" }, responses[0].Answers.Select(a => a.FieldName));
    }
}
