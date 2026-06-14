using System;
using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Base for every 切り口 that carries the 対象期間 (date-range) picker shown top-right — the same picker the
// dashboard uses (DateRangeSelection). The range only narrows which responses are counted. Subclasses
// implement Reload to re-run their aggregation when the range changes.
public abstract class PeriodScopedViewModel : ViewModelBase
{
    private readonly DateRangeSelection _range = new();

    // Current 対象期間 — read by the picker control to seed and label itself.
    public DateRangePreset Preset => _range.Preset;
    public DateTime From => _range.From;
    public DateTime To => _range.To;
    public string RangeLabel => _range.Label;

    // The [from, to] date_key window for the current selection.
    protected (long FromKey, long ToKey) Window => _range.DateKeyWindow;

    // Applies a range chosen in the picker, then re-runs the slice's aggregation.
    public void SetRange(DateRangePreset preset, DateTime from, DateTime to)
    {
        _range.Set(preset, from, to);
        Reload();
    }

    protected abstract void Reload();

    // One analysis column per project field (aggregation chosen from its type), skipping the field
    // that is the grouping dimension — it is the rows, so it is not also a column.
    protected static IReadOnlyList<AnalysisColumn> ColumnsFor(Project project, string? excludeField) =>
        project.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name != excludeField)
            .Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f)))
            .ToList();
}
