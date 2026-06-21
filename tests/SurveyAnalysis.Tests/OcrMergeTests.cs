using System.Collections.Generic;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// OcrExtractor.MergeValues combines the per-band OCR readings of one tiled image: a 選択肢 field's options
// are unioned across the bands it appears in (so a multi-select split across a band cut stays complete),
// while every other field takes its longest non-empty reading (the band that saw the whole value wins).
public class OcrMergeTests
{
    private static IReadOnlyDictionary<string, string> Band(params (string Field, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>();
        foreach (var (field, value) in pairs)
            map[field] = value;
        return map;
    }

    [Fact]
    public void Unions_choice_options_and_takes_the_longest_other_reading()
    {
        var fields = new List<DataField>
        {
            new() { Name = "氏名", FieldType = FieldType.Name },
            new() { Name = "きっかけ", FieldType = FieldType.Choice },
            new() { Name = "感想", FieldType = FieldType.FreeText },
        };

        var bands = new List<IReadOnlyDictionary<string, string>>
        {
            Band(("きっかけ", "テレビ; ネット"), ("感想", "良い")),
            Band(("氏名", "山田 太郎"), ("きっかけ", "ネット; 電話")),
            Band(("感想", "とても良い感想です。")),
        };

        var merged = OcrExtractor.MergeValues(bands, fields);

        Assert.Equal("山田 太郎", merged["氏名"]);
        Assert.Equal("テレビ; ネット; 電話", merged["きっかけ"]);   // union, first-seen order, deduped
        Assert.Equal("とても良い感想です。", merged["感想"]);        // the longer of the two readings
    }

    [Fact]
    public void Omits_fields_no_band_read()
    {
        var fields = new List<DataField>
        {
            new() { Name = "電話番号", FieldType = FieldType.Phone },
            new() { Name = "満足度", FieldType = FieldType.Choice },
        };
        var bands = new List<IReadOnlyDictionary<string, string>> { Band(("満足度", "")) };

        var merged = OcrExtractor.MergeValues(bands, fields);

        Assert.False(merged.ContainsKey("電話番号"));   // never read
        Assert.False(merged.ContainsKey("満足度"));     // only blank readings
    }
}
