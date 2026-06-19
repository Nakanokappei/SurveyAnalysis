using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// 時間別 → 曜日: a flat analysis table grouping responses by day of week (月→日) within the 集計期間
// window, with every project field as a column. Clicking a weekday drills to its 個票一覧 (記入日 +
// 抜粋, PII hidden); the clickable breadcrumb (曜日 ＞ 月曜日) walks back. The star is refreshed on open.
public partial class WeekdaySliceViewModel : PeriodScopedViewModel, ISliceView
{
    // dim_date day_of_week order: 0=月 … 6=日. Used to map a clicked row label back to its weekday.
    private static readonly string[] DayLabels =
        { "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日" };

    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;
    private readonly bool _hasDateField;
    private readonly string? _dateFieldName;
    private readonly string? _excerptFieldName;

    public IReadOnlyList<AnalysisColumn> Columns { get; }
    public ObservableCollection<AnalysisRow> Rows { get; } = new();
    public ObservableCollection<SurveyRow> Responses { get; } = new();
    public ObservableCollection<Crumb> Breadcrumbs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTotal))]
    private AnalysisRow? _totalRow;

    public bool HasTotal => TotalRow is not null;

    [ObservableProperty]
    private string _countSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ShowResponses))]
    private bool _hasData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ShowResponses))]
    private bool _isResponseView;

    [ObservableProperty]
    private string _emptyMessage = "";

    public bool ShowRows => HasData && !IsResponseView;
    public bool ShowResponses => HasData && IsResponseView;

    // ISliceView mapping (members not already matching the interface by name/type).
    string ISliceView.Summary => CountSummary;
    ICommand ISliceView.DrillIntoCommand => DrillIntoCommand;
    ICommand ISliceView.NavigateCrumbCommand => NavigateCrumbCommand;

    public WeekdaySliceViewModel(Project project, AnalyticsRepository analytics)
    {
        _analytics = analytics;
        _projectId = project.Id;
        _dateFieldName = AnalyticsRepository.DateField(project);
        // The time basis is the rows (曜日), so the date field is not also shown as a column.
        Columns = ColumnsFor(project, _dateFieldName);

        _excerptFieldName =
            project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        analytics.Rebuild(project);

        _hasDateField = _dateFieldName is not null;
        if (!_hasDateField)
        {
            EmptyMessage = "このプロジェクトには集計の基準日（日付項目）がありません。「データ項目」で設定できます。";
            return;
        }

        Reload();
    }

    // Drill from a weekday into its individual responses.
    [RelayCommand]
    private void DrillInto(AnalysisRow? row)
    {
        if (row is null)
            return;
        var dayOfWeek = Array.IndexOf(DayLabels, row.Label);
        if (dayOfWeek < 0)
            return;
        ShowResponsesFor(dayOfWeek, row.Label);
    }

    // Clicking a breadcrumb segment: only 曜日 (index 0) is a target — it returns to the weekday list.
    [RelayCommand]
    private void NavigateCrumb(Crumb? crumb)
    {
        if (crumb is { Index: 0 })
            Reload();
    }

    // Changing the 集計期間 resets to the weekday list for the new window.
    protected override void Reload()
    {
        if (!_hasDateField)
            return;

        IsResponseView = false;
        var (from, to) = Window;
        SentimentTrend = _analytics.SentimentTrend(_projectId, from, to);
        var table = _analytics.AggregateRows(_projectId, AnalysisGrouping.Weekday, TimeScope.Root, from, to, Columns);

        Rows.Clear();
        foreach (var row in table.Rows)
            Rows.Add(row);
        TotalRow = table.Rows.Count > 0 ? table.Total : null;

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb("曜日", 0));

        var total = table.Rows.Sum(r => r.Count);
        CountSummary = $"合計 {total} 件";
        HasData = table.Rows.Count > 0;
        EmptyMessage = HasData ? "" : "この集計期間には回答がありません。右上の集計期間を広げてください。";
    }

    private void ShowResponsesFor(int dayOfWeek, string label)
    {
        var (from, to) = Window;
        TotalRow = null; // the 個票 list has no column table
        Responses.Clear();
        foreach (var response in _analytics.ResponsesWithAnalysisForWeekday(_projectId, dayOfWeek, from, to))
            Responses.Add(ResponseRowFactory.Build(_dateFieldName, _excerptFieldName, response));

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb("曜日", 0));
        Breadcrumbs.Add(new Crumb("＞  " + label, 1));

        CountSummary = $"合計 {Responses.Count} 件";
        IsResponseView = true;
        HasData = Responses.Count > 0;
        EmptyMessage = HasData ? "" : "この曜日には回答がありません。";
    }
}
