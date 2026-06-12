namespace SurveyAnalysis.Models;

// One horizontal bar in a dashboard chart. BarWidth is pre-scaled to pixels by the
// view model so the bar rectangles can bind to it directly without a converter.
public class BarItem
{
    public required string Label { get; init; }
    public required int Count { get; init; }
    public required double BarWidth { get; init; }
    public string Accent { get; init; } = "#005FB8";
}

// One row in the dashboard table. Excerpt is a free-text snippet only; personal
// information is never surfaced here, matching the spec's hide-PII requirement.
public class SurveyRow
{
    public required string EntryDate { get; init; }
    public required string Topic { get; init; }
    public required string Sentiment { get; init; }
    public required string Excerpt { get; init; }
}
