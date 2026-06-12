namespace SurveyAnalysis.Models;

// A position in the time drill-down: how deep we are and the accumulated filters that pin the path.
// Depth 0 = 全期間 (children grouped by 年度), 1 = a 年度 (children = 月), 2 = a 月 (children = 週),
// 3 = a 週 (children = 日), 4 = a 日 (terminal: the individual responses, no further drill).
// Each level carries the predicate chosen above it, so a scope's responses are always a subset of
// its parent's — the parent's count equals the sum of its children. Label is the breadcrumb crumb
// for this scope (e.g. "2026年5月").
public sealed record TimeScope(
    int Depth,
    long? FiscalYear = null,
    int? Year = null,
    int? Month = null,
    int? WeekYear = null,
    int? WeekOfYear = null,
    long? DateKey = null,
    string Label = "全期間")
{
    public static TimeScope Root => new(0);

    // At the 日 level there is nothing finer to group by, so we list responses instead.
    public bool IsTerminal => Depth >= 4;
}

// One child group under a scope: its label and count, plus the scope you land in when you click it.
public sealed record TimeChild(string Label, int Count, TimeScope Scope);

// One clickable breadcrumb segment: the text to show (its leading "＞" separator already baked in)
// and the path depth it returns to when clicked.
public sealed record Crumb(string Display, int Index);
