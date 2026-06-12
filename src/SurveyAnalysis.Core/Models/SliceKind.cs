namespace SurveyAnalysis.Models;

// The dimensions the analytics star schema can be sliced by — the "切り口" shown in the sidebar.
// Each maps to a project field role: time ← the aggregation date field, region ← an address /
// prefecture / city field, topic ← a topic-assignment field (populated once LLM analysis runs).
public enum SliceKind
{
    Time,    // 時間別
    Region,  // 地域別
    Topic    // トピック別
}

// Japanese labels for the slices, kept next to the enum so the UI wording stays greppable.
public static class SliceInfo
{
    public static string Label(SliceKind kind) => kind switch
    {
        SliceKind.Time => "時間別",
        SliceKind.Region => "地域別",
        SliceKind.Topic => "トピック別",
        _ => kind.ToString()
    };
}
