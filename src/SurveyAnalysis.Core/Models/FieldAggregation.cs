using System.Collections.Generic;

namespace SurveyAnalysis.Models;

// How a data field is summarised in the analysis tables. Chosen automatically from the field's type
// (no per-field UI): numbers add up or average, sentiment averages its LLM score, and everything
// else counts how many distinct values appeared. 件数 (response count) is intentionally not a column.
public enum FieldAggregation
{
    DistinctCount,     // 種類数: number of distinct non-empty values in the group
    Sum,               // 合計: sum of the numeric values
    Average,           // 平均: average of the numeric values
    SentimentAverage,  // 平均(感情): average of fact_response.sentiment_score (— until LLM)
}

public static class FieldAggregationInfo
{
    // The aggregation for a field, derived from its type. Sentiment-analysis fields average their
    // score; 数値 sums; every other type (選択肢・テキスト・文章 etc.) counts distinct values.
    public static FieldAggregation For(DataField field)
    {
        if (field.Analysis == AnalysisMethod.Sentiment)
            return FieldAggregation.SentimentAverage;
        return field.FieldType switch
        {
            FieldType.Number => FieldAggregation.Sum,
            _ => FieldAggregation.DistinctCount,
        };
    }

    // Short label shown under the column name so the reader knows what the number means.
    public static string Label(FieldAggregation aggregation) => aggregation switch
    {
        FieldAggregation.DistinctCount => "種類数",
        FieldAggregation.Sum => "合計",
        FieldAggregation.Average => "平均",
        FieldAggregation.SentimentAverage => "平均",
        _ => "",
    };
}

// One column of the analysis table: a project field and how it is aggregated. AggregationLabel is the
// short word shown under the field name (種類数 / 合計 / 平均).
public sealed record AnalysisColumn(string Name, FieldAggregation Aggregation)
{
    public string AggregationLabel => FieldAggregationInfo.Label(Aggregation);
}

// One row of the analysis table: the dimension label, one formatted cell per AnalysisColumn (aligned
// by position), the response count (drives the summary, not a column), the group's average 感情極性
// (a dedicated column shown in every report — "+0.00" / "—"), and — for the drillable time dimension —
// the scope to open when the row is clicked.
public sealed record AnalysisRow(string Label, IReadOnlyList<string> Cells, int Count, TimeScope? ChildScope, string Sentiment = "—");

// The full analysis table: the per-group rows plus a 全体 total row. The total row aggregates every
// column over all responses with that column's own method — 種類数 counts the distinct values across
// the whole set (not the response count), 合計 sums, 平均 / 感情平均 average.
public sealed record AnalysisTable(IReadOnlyList<AnalysisRow> Rows, AnalysisRow Total);

// Which dimension the rows are grouped by. Time also carries a drill scope; the others are flat.
// Choice groups by the values of one 選択肢 field (its id is passed alongside).
public enum AnalysisGrouping
{
    Time,
    Weekday,
    Region,
    Topic,
    Choice,
}
