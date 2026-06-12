using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Base for every 切り口 that carries the 集計期間 (date-window) dropdown shown top-right. The window is
// the last N days ending today; it only narrows which responses are counted. Subclasses implement
// Reload to re-run their aggregation when the period changes.
public abstract partial class PeriodScopedViewModel : ViewModelBase
{
    // The dropdown choices and the current selection (default 90日).
    public AggregationPeriod[] Periods => AggregationPeriodInfo.All;

    [ObservableProperty]
    private AggregationPeriod _selectedPeriod = AggregationPeriod.Days90;

    // The [from, to] date_key window for the current selection, anchored at today.
    protected (long FromKey, long ToKey) Window => AggregationPeriodInfo.Window(SelectedPeriod, DateTime.Today);

    partial void OnSelectedPeriodChanged(AggregationPeriod value) => Reload();

    protected abstract void Reload();

    // One analysis column per project field (aggregation chosen from its type), skipping the field
    // that is the grouping dimension — it is the rows, so it is not also a column.
    protected static IReadOnlyList<AnalysisColumn> ColumnsFor(Project project, string? excludeField) =>
        project.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name != excludeField)
            .Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f)))
            .ToList();
}
