using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// 地域別 / トピック別 / 選択肢別: a flat analysis table grouping responses by region, topic or 選択肢 option
// within the 集計期間 window, with a 感情極性 column and a 件数 column (the dimension summary), or — when
// 地域別 is cross-tabbed against a 質問 — one count column per that question's category (+ 合計). A trailing
// 全体 total row holds the column totals. Clicking a group drills into its 個票一覧 (記入日 / トピック / 感情 /
// 抜粋, PII hidden); a breadcrumb walks back. Region groups by 都道府県 (missing → （未設定）); topic groups by
// the per-自由記述-column topic (its own sentiment); choice groups by the per-選択肢-column option (multi-
// select split). 時間別 / 曜日別 are their own views (TimeSliceViewModel / WeekdaySliceViewModel).
public partial class SliceViewModel : PeriodScopedViewModel, ISliceView
{
    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;
    private readonly AnalysisGrouping _grouping;
    private readonly string? _dateFieldName;
    private readonly string? _excerptFieldName;
    // > 0 when scoped to one 質問 column: the 自由記述 column for トピック別, the 選択肢 column for 選択肢別.
    // 0 for 地域別 (no scoping question).
    private readonly long _questionFieldId;
    // Non-null when 地域別 is cross-tabbed against a 質問; its categories are the columns. Null otherwise.
    private readonly CrossTabSpec? _crossTab;
    private readonly IReadOnlyList<string>? _categories;

    public string Title { get; }
    public string Description { get; }
    // The leading (dimension) column heading: 地域 / トピック / 選択肢.
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

    // ISliceView mapping (members not already matching the interface by name/type).
    string ISliceView.Summary => CountSummary;
    ICommand ISliceView.DrillIntoCommand => DrillIntoCommand;
    ICommand ISliceView.NavigateCrumbCommand => NavigateCrumbCommand;

    // topicFieldId > 0 makes this a topic report for one 質問 (自由記述 column): its topics are the rows,
    // its per-column sentiment the 感情極性, and the report title is the question name. The トピック別 sidebar
    // entry opens one of these per 自由記述 question (a dynamic submenu).
    public SliceViewModel(Project project, AnalyticsRepository analytics, SliceKind kind, long questionFieldId = 0, CrossTabSpec? crossTab = null)
    {
        _analytics = analytics;
        _projectId = project.Id;
        _grouping = kind switch
        {
            SliceKind.Topic => AnalysisGrouping.Topic,
            SliceKind.Choice => AnalysisGrouping.Choice,
            _ => AnalysisGrouping.Region,
        };
        _questionFieldId = questionFieldId;
        _crossTab = crossTab;
        DimensionTitle = kind switch
        {
            SliceKind.Topic => "トピック",
            SliceKind.Choice => "選択肢",
            _ => "地域",
        };

        var questionField = questionFieldId > 0 ? project.Fields.FirstOrDefault(f => f.Id == questionFieldId) : null;

        Title = kind switch
        {
            SliceKind.Region when crossTab is not null => $"地域別 ・ {crossTab.Name}",
            SliceKind.Region => "地域別",
            _ => questionField?.Name ?? SliceInfo.Label(kind),
        };
        Description = kind switch
        {
            SliceKind.Region when crossTab is not null => CrossTabDescription("地域", crossTab),
            SliceKind.Region => "回答を地域（住所・都道府県・市区町村）ごとに集計します。行をクリックすると個票を表示します。",
            SliceKind.Topic when questionField is not null => $"「{questionField.Name}」の回答をトピックごとに集計します。感情極性はトピックごとの平均です。行をクリックすると個票を表示します。",
            SliceKind.Topic => "回答をトピックごとに集計します。感情極性はトピックごとの平均です。行をクリックすると個票を表示します。",
            SliceKind.Choice when questionField is not null => $"「{questionField.Name}」の回答を選択肢ごとに集計します（複数選択は各オプションに計上）。行をクリックすると個票を表示します。",
            SliceKind.Choice => "回答を選択肢ごとに集計します（複数選択は各オプションに計上）。行をクリックすると個票を表示します。",
            _ => ""
        };

        // 個票 fields: 記入日 from the aggregation date; the excerpt is the scoped 自由記述 question's text
        // (トピック別) else the first free-text / sentiment field (選択肢別・地域別 have no text question).
        _dateFieldName = AnalyticsRepository.DateField(project);
        _excerptFieldName = (kind == SliceKind.Topic ? questionField?.Name : null)
            ?? project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        // Keep the star current with the latest imported responses (cross-tab columns read its dictionaries).
        analytics.Rebuild(project);

        // Columns: a cross-tab's category counts (+ 合計), else just 件数 for the dimension summary.
        if (crossTab is not null)
        {
            _categories = analytics.CrossTabCategories(crossTab);
            Columns = CrossTabColumns(_categories);
        }
        else
            Columns = CountColumn;

        Reload();
    }

    // Drill from a group (region / topic) into its individual responses.
    [RelayCommand]
    private void DrillInto(AnalysisRow? row)
    {
        if (row is null)
            return;

        var (from, to) = Window;
        var responses = _grouping switch
        {
            AnalysisGrouping.Topic => _analytics.ResponsesWithAnalysisForTopic(_projectId, row.Label, from, to, _questionFieldId > 0 ? _questionFieldId : null),
            AnalysisGrouping.Choice => _analytics.ResponsesWithAnalysisForChoice(_projectId, _questionFieldId, row.Label, from, to),
            _ => _analytics.ResponsesWithAnalysisForRegion(_projectId, row.Label, from, to),
        };

        // The trend follows the drill: scoped to the selected region / topic / option.
        SentimentTrend = _grouping switch
        {
            AnalysisGrouping.Topic => _analytics.SentimentTrendForTopic(_projectId, row.Label, from, to, _questionFieldId > 0 ? _questionFieldId : null),
            AnalysisGrouping.Choice => _analytics.SentimentTrendForChoice(_projectId, _questionFieldId, row.Label, from, to),
            _ => _analytics.SentimentTrendForRegion(_projectId, row.Label, from, to),
        };

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
        var table = _crossTab is not null
            ? _analytics.AggregateCrossTab(_projectId, AnalysisGrouping.Region, TimeScope.Root, from, to, _crossTab, _categories!)
            : _analytics.AggregateRows(_projectId, _grouping, TimeScope.Root, from, to, Columns,
                choiceFieldId: _grouping == AnalysisGrouping.Choice ? _questionFieldId : null,
                topicFieldId: _grouping == AnalysisGrouping.Topic && _questionFieldId > 0 ? _questionFieldId : null);

        Rows.Clear();
        foreach (var row in table.Rows)
            Rows.Add(row);
        TotalRow = table.Rows.Count > 0 ? table.Total : null;

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb(DimensionTitle, 0));

        // 件数 = distinct responses (the 全体 row's count), not the sum of group counts: a multi-select
        // 選択肢 response falls in several option groups, so summing the rows would over-count it.
        CountSummary = $"合計 {table.Total.Count} 件 ・ {table.Rows.Count} グループ";
        HasData = table.Rows.Count > 0;
        EmptyMessage = HasData ? "" : "この集計期間には回答がありません。右上の集計期間を広げてください。";
    }
}
