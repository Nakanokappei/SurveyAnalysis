using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// 時間別 → 期間: a drill-down analysis table. Start at 全期間 grouped by 年度, click a year to see its
// 月別 breakdown, a month for 週別, a week for 日別, and a day for the 個票一覧 (記入日 + 抜粋, PII hidden).
// Every project field is a column, aggregated by its type (種類数 / 合計 / 平均). A breadcrumb plus 戻る
// walks back up; the 集計期間 dropdown (from the base) narrows the window and resets the drill.
public partial class TimeSliceViewModel : PeriodScopedViewModel
{
    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;
    private readonly string? _dateFieldName;
    private readonly string? _excerptFieldName;

    // One column per project field, with the aggregation chosen from its type.
    public IReadOnlyList<AnalysisColumn> Columns { get; }

    // The drill path; the last entry is the current scope. Drives the breadcrumb and 戻る.
    private readonly List<TimeScope> _path = new();

    // The clickable child rows (empty at the 日 terminal).
    public ObservableCollection<AnalysisRow> Rows { get; } = new();

    // The individual responses, shown only at the 日 terminal.
    public ObservableCollection<SurveyRow> Responses { get; } = new();

    // The 全体 total row (null at the 個票 terminal, which has no column table).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTotal))]
    private AnalysisRow? _totalRow;

    public bool HasTotal => TotalRow is not null;

    // Clickable breadcrumb segments (全期間 ＞ 2026年度 ＞ …); clicking one returns to that depth.
    public ObservableCollection<Crumb> Breadcrumbs { get; } = new();

    [ObservableProperty]
    private string _childLevelTitle = "";

    [ObservableProperty]
    private string _scopeSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DrillUpCommand))]
    private bool _canDrillUp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren))]
    [NotifyPropertyChangedFor(nameof(ShowResponses))]
    private bool _isTerminal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChildren))]
    [NotifyPropertyChangedFor(nameof(ShowResponses))]
    private bool _hasData;

    [ObservableProperty]
    private string _emptyMessage = "";

    // The main table shows the clickable child breakdown except at the 日 terminal, where it lists the
    // individual responses instead.
    public bool ShowChildren => HasData && !IsTerminal;
    public bool ShowResponses => HasData && IsTerminal;

    public TimeSliceViewModel(Project project, AnalyticsRepository analytics)
    {
        _analytics = analytics;
        _projectId = project.Id;

        // The aggregation date field drives the hierarchy; the excerpt comes from a free-text (or
        // sentiment) field — never from a personal-information field.
        _dateFieldName = AnalyticsRepository.DateField(project);
        // The grouping field (the time basis) is the rows, so it is not also shown as a column.
        Columns = project.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name != _dateFieldName)
            .Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f)))
            .ToList();
        _excerptFieldName =
            project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        // Keep the star current with the latest imported responses.
        analytics.Rebuild(project);

        // Without a date field there is no time hierarchy to walk.
        if (_dateFieldName is null)
        {
            EmptyMessage = "このプロジェクトには集計の基準日（日付項目）がありません。「データ項目」で設定できます。";
            return;
        }

        _path.Add(TimeScope.Root);
        Load();
    }

    // Drill into a child row: push its scope and reload.
    [RelayCommand]
    private void DrillInto(AnalysisRow? row)
    {
        if (row?.ChildScope is not { } child || IsTerminal)
            return;
        _path.Add(child);
        Load();
    }

    // 戻る: pop one level (never past 全期間).
    [RelayCommand(CanExecute = nameof(CanDrillUp))]
    private void DrillUp()
    {
        if (_path.Count <= 1)
            return;
        _path.RemoveAt(_path.Count - 1);
        Load();
    }

    // Clicking a breadcrumb segment returns to that depth (drops everything below it).
    [RelayCommand]
    private void NavigateCrumb(Crumb? crumb)
    {
        if (crumb is null || crumb.Index >= _path.Count - 1)
            return;
        _path.RemoveRange(crumb.Index + 1, _path.Count - crumb.Index - 1);
        Load();
    }

    // Changing the 集計期間 changes which periods exist, so reset the drill to 全期間 and reload.
    protected override void Reload()
    {
        if (_dateFieldName is null)
            return;
        _path.Clear();
        _path.Add(TimeScope.Root);
        Load();
    }

    // Loads the current scope within the selected window: either the child breakdown or — at the 日
    // terminal — the individual responses.
    private void Load()
    {
        var scope = _path[^1];
        var (from, to) = Window;
        CanDrillUp = _path.Count > 1;
        IsTerminal = scope.IsTerminal;

        Breadcrumbs.Clear();
        for (var i = 0; i < _path.Count; i++)
            Breadcrumbs.Add(new Crumb(i == 0 ? _path[i].Label : "＞  " + _path[i].Label, i));

        Rows.Clear();
        Responses.Clear();

        if (IsTerminal)
        {
            TotalRow = null;
            foreach (var response in _analytics.ResponsesForScope(_projectId, scope, from, to))
                Responses.Add(ResponseRowFactory.Build(_dateFieldName, _excerptFieldName, response));
            ChildLevelTitle = "回答一覧";
            ScopeSummary = $"合計 {Responses.Count} 件";
            HasData = Responses.Count > 0;
            EmptyMessage = HasData ? "" : "この日には回答がありません。";
            return;
        }

        var table = _analytics.AggregateRows(_projectId, AnalysisGrouping.Time, scope, from, to, Columns);
        foreach (var row in table.Rows)
            Rows.Add(row);
        TotalRow = table.Rows.Count > 0 ? table.Total : null;

        var total = table.Rows.Sum(r => r.Count);
        ChildLevelTitle = ChildLevelName(scope.Depth);
        ScopeSummary = $"合計 {total} 件 ・ {table.Rows.Count} グループ";
        HasData = table.Rows.Count > 0;
        EmptyMessage = HasData
            ? ""
            : "この集計期間には回答がありません。右上の集計期間を広げるか、「インポート (CSV)」から取り込めます。";
    }

    // The name of the level one step below the given depth (shown above the child table).
    private static string ChildLevelName(int depth) => depth switch
    {
        0 => "年度別",
        1 => "月別",
        2 => "週別",
        _ => "日別",
    };
}
