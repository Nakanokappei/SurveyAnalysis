using System;
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
}
