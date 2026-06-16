using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

// Splitting a multi-select 選択肢 cell ("A; B; C") into individual options.
public class ChoiceValuesTests
{
    [Theory]
    [InlineData("テレビ; インターネット; 電話", new[] { "テレビ", "インターネット", "電話" })]
    [InlineData("普通", new[] { "普通" })]                                   // single-select, no separator
    [InlineData("A;B ; C", new[] { "A", "B", "C" })]                         // tolerant of spacing
    [InlineData("テレビ; テレビ", new[] { "テレビ" })]                        // de-duplicated
    [InlineData("その他: チラシを見て", new[] { "その他: チラシを見て" })]    // write-in (no ';') stays whole
    public void Split_returns_trimmed_distinct_options(string value, string[] expected)
    {
        Assert.Equal(expected, ChoiceValues.Split(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Split_returns_empty_for_blank(string? value)
    {
        Assert.Empty(ChoiceValues.Split(value));
    }
}
