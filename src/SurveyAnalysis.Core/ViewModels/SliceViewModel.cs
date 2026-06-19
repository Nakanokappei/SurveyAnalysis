using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// 地域別 / トピック別: a flat analysis table grouping responses by region or topic within the 集計期間
// window, every project field a column plus a 感情極性 column, with a trailing 全体 total row. Clicking a
// group drills into its 個票一覧 (記入日 / トピック / 感情 / 抜粋, PII hidden); a breadcrumb walks back.
// Region groups by 都道府県 etc. (missing → （未設定）); topic groups by the per-自由記述-column topic and
// shows that topic's own sentiment. 時間別 is its own drill-down view (TimeSliceViewModel / Weekday…).
public partial class SliceViewModel : PeriodScopedViewModel
{
    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;
    private readonly AnalysisGrouping _grouping;
    private readonly string? _dateFieldName;
    private readonly string? _excerptFieldName;

    public string Title { get; }
    public string Description { get; }
    // The leading (dimension) column heading: 地域 or トピック.
    public string DimensionTitle { get; }

    public IReadOnlyList<AnalysisColumn> Columns { get; }
    public ObservableCollection<AnalysisRow> Rows { get; } = new();
    public ObservableCollection<SurveyRow> Responses { get; } = new();
    public ObservableCollection<Crumb> Breadcrumbs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTotal))]
    private AnalysisRow? _totalRow;

    public bool HasTotal => TotalRow is not null;

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

    [ObservableProperty]
    private string _countSummary = "";

    // The group table shows except when drilled into a group's 個票, which lists the responses instead.
    public bool ShowRows => HasData && !IsResponseView;
    public bool ShowResponses => HasData && IsResponseView;

    public SliceViewModel(Project project, AnalyticsRepository analytics, SliceKind kind)
    {
        _analytics = analytics;
        _projectId = project.Id;
        _grouping = kind == SliceKind.Topic ? AnalysisGrouping.Topic : AnalysisGrouping.Region;
        DimensionTitle = kind == SliceKind.Topic ? "トピック" : "地域";

        Title = SliceInfo.Label(kind);
        Description = kind switch
        {
            SliceKind.Region => "回答を地域（住所・都道府県・市区町村）ごとに集計します。行をクリックすると個票を表示します。",
            SliceKind.Topic => "回答をトピックごとに集計します。感情極性はトピックごとの平均です。行をクリックすると個票を表示します。",
            _ => ""
        };

        // The grouping field (都道府県/住所/市区町村 for 地域, the topic field for トピック) is the rows,
        // so it is not also shown as a column.
        var groupingField = kind == SliceKind.Topic
            ? AnalyticsRepository.TopicField(project)
            : AnalyticsRepository.RegionField(project);
        Columns = ColumnsFor(project, groupingField);

        // 個票 fields: 記入日 from the aggregation date, the excerpt from a free-text (or sentiment) field.
        _dateFieldName = AnalyticsRepository.DateField(project);
        _excerptFieldName =
            project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        // Keep the star current with the latest imported responses.
        analytics.Rebuild(project);

        Reload();
    }

    // Drill from a group (region / topic) into its individual responses.
    [RelayCommand]
    private void DrillInto(AnalysisRow? row)
    {
        if (row is null)
            return;

        var (from, to) = Window;
        var responses = _grouping == AnalysisGrouping.Topic
            ? _analytics.ResponsesWithAnalysisForTopic(_projectId, row.Label, from, to)
            : _analytics.ResponsesWithAnalysisForRegion(_projectId, row.Label, from, to);

        TotalRow = null;   // the 個票 list has no column table
        Responses.Clear();
        foreach (var response in responses)
            Responses.Add(ResponseRowFactory.Build(_dateFieldName, _excerptFieldName, response));

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb(DimensionTitle, 0));
        Breadcrumbs.Add(new Crumb("＞  " + row.Label, 1));

        CountSummary = $"合計 {Responses.Count} 件";
        IsResponseView = true;
        HasData = Responses.Count > 0;
        EmptyMessage = HasData ? "" : "この区分には回答がありません。";
    }

    // Clicking a breadcrumb segment: only the dimension root (index 0) is a target — it returns to the list.
    [RelayCommand]
    private void NavigateCrumb(Crumb? crumb)
    {
        if (crumb is { Index: 0 })
            Reload();
    }

    // Re-aggregate within the selected 集計期間 window (also the way back from a drilled 個票).
    protected override void Reload()
    {
        IsResponseView = false;
        var (from, to) = Window;
        SentimentTrend = _analytics.SentimentTrend(_projectId, from, to);
        var table = _analytics.AggregateRows(_projectId, _grouping, TimeScope.Root, from, to, Columns);

        Rows.Clear();
        foreach (var row in table.Rows)
            Rows.Add(row);
        TotalRow = table.Rows.Count > 0 ? table.Total : null;

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb(DimensionTitle, 0));

        var total = table.Rows.Sum(r => r.Count);
        CountSummary = $"合計 {total} 件 ・ {table.Rows.Count} グループ";
        HasData = table.Rows.Count > 0;
        EmptyMessage = HasData ? "" : "この集計期間には回答がありません。右上の集計期間を広げてください。";
    }
}
