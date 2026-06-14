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

// The dashboard shown in the main pane: KPIs, a topic bar chart, a sentiment bar chart, and a
// table — for the selected month. Saved projects aggregate their real imported responses; the
// bundled sample project (not persisted, Id 0) keeps the illustrative dummy data so the full
// layout stays reviewable. Topic and sentiment need LLM analysis (not yet implemented), so for
// real projects those are shown as pending; total responses and the answer list are real.
public partial class DashboardViewModel : ViewModelBase
{
    private const double MaxBarWidth = 180;

    private readonly Project _project;
    private readonly bool _isSample;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _loaded;
    private readonly string? _aggregationFieldName;
    private readonly string? _excerptFieldName;
    private string? _drilledTopic;

    // The active 対象期間 (date range): a preset window or a custom [from, to] picked on the calendar.
    private DateRangePreset _preset = DateRangePreset.Last30Days;
    private DateTime _from;
    private DateTime _to;

    public DateRangePreset Preset => _preset;
    public DateTime From => _from;
    public DateTime To => _to;

    // The picker button's text: the preset's name, or the custom range as dates.
    public string RangeLabel => _preset == DateRangePreset.Custom
        ? $"{_from:yyyy/MM/dd} 〜 {_to:yyyy/MM/dd}"
        : DateRangePresetInfo.Label(_preset);

    // Breadcrumb shown above the charts, e.g. "直近30日" or "直近30日 ＞ 配線・接続".
    [ObservableProperty]
    private string _breadcrumb = "";

    [ObservableProperty]
    private string _levelTitle = "";

    // True only when drilled into a topic; shows the 集計に戻る (drill-up) button.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDrillHint))]
    private bool _canDrillUp;

    // True for real projects: topic/sentiment analytics await LLM, so they are shown as pending.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDrillHint))]
    private bool _analysisPending;

    // True for a saved project with no responses in the selected month (drives the empty-state hint).
    [ObservableProperty]
    private bool _hasNoResponses;

    // The "click a bar to drill down" hint only applies to the sample's clickable chart.
    public bool ShowDrillHint => !CanDrillUp && !AnalysisPending;

    // KPI cards.
    [ObservableProperty]
    private int _totalResponses;

    // Shown as "—" until sentiment analysis (LLM) is wired; a number in the sample.
    [ObservableProperty]
    private string _negativeDisplay = "—";

    [ObservableProperty]
    private string _averageSentiment = "";

    public ObservableCollection<BarItem> TopicBars { get; } = new();
    public ObservableCollection<BarItem> SentimentBars { get; } = new();
    public ObservableCollection<SurveyRow> Rows { get; } = new();

    public DashboardViewModel(Project project, ResponseRepository responses)
    {
        _project = project;

        // Default 対象期間 = 直近30日 (ending today).
        var (from, to) = DateRangePresetInfo.Range(DateRangePreset.Last30Days, DateTime.Today)!.Value;
        _from = from;
        _to = to;

        // The aggregation date field gives each response its 記入日 and decides whether it falls in the
        // selected range; the excerpt comes from a free-text (or sentiment) field — never from PII.
        _aggregationFieldName = project.Fields.FirstOrDefault(f => f.UseForAggregation)?.Name;
        _excerptFieldName =
            project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        _isSample = project.Id == 0;
        _loaded = _isSample
            ? Array.Empty<IReadOnlyDictionary<string, string>>()
            : responses.LoadForProject(project.Id);

        ShowOverview();
    }

    // Applies a new 対象期間 chosen in the picker, then rebuilds the overview. Custom carries the user's
    // [from, to]; a preset's range is recomputed by the caller (the picker) so it stays anchored at today.
    public void SetRange(DateRangePreset preset, DateTime from, DateTime to)
    {
        _preset = preset;
        _from = from.Date;
        _to = to.Date;
        OnPropertyChanged(nameof(RangeLabel));
        ShowOverview();
    }

    // Builds the period overview, dispatching to the sample (dummy) or real aggregation.
    private void ShowOverview()
    {
        _drilledTopic = null;
        CanDrillUp = false;
        Breadcrumb = RangeLabel;
        LevelTitle = "トピック別 件数";

        if (_isSample)
            ShowSampleOverview();
        else
            ShowRealOverview();
    }

    // Real overview: total responses and the answer list come from imported data; topic/sentiment
    // analytics are left empty and marked pending until LLM analysis exists.
    private void ShowRealOverview()
    {
        AnalysisPending = true;
        NegativeDisplay = "—";
        AverageSentiment = "—";
        TopicBars.Clear();
        SentimentBars.Clear();

        var inRange = FilterByRange(_loaded);
        TotalResponses = inRange.Count;
        HasNoResponses = inRange.Count == 0;

        Rows.Clear();
        foreach (var response in inRange)
            Rows.Add(BuildRow(response));
    }

    // Keeps only responses whose aggregation-date answer falls in the selected [from, to] range
    // (inclusive). With no aggregation field the date cannot be determined, so all responses are shown.
    private IReadOnlyList<IReadOnlyDictionary<string, string>> FilterByRange(
        IReadOnlyList<IReadOnlyDictionary<string, string>> responses)
    {
        if (_aggregationFieldName is null)
            return responses;

        var result = new List<IReadOnlyDictionary<string, string>>();
        foreach (var response in responses)
        {
            response.TryGetValue(_aggregationFieldName, out var dateValue);
            if (TryParseDate(dateValue, out var date) && date.Date >= _from && date.Date <= _to)
                result.Add(response);
        }
        return result;
    }

    // Builds one table row from a response: 記入日 from the aggregation date, the excerpt from the
    // free-text field, and topic/sentiment pending LLM. Personal information is never shown.
    private SurveyRow BuildRow(IReadOnlyDictionary<string, string> response)
    {
        var entryDate = "—";
        if (_aggregationFieldName is not null && response.TryGetValue(_aggregationFieldName, out var date))
            entryDate = FormatDate(date);

        var excerpt = "";
        if (_excerptFieldName is not null && response.TryGetValue(_excerptFieldName, out var text))
            excerpt = Truncate(text, 40);

        return new SurveyRow { EntryDate = entryDate, Topic = "—", Sentiment = "—", Excerpt = excerpt };
    }

    // ===== Sample (dummy) dashboard, preserved for layout review =====

    private void ShowSampleOverview()
    {
        AnalysisPending = false;
        HasNoResponses = false;

        ReplaceBars(TopicBars, SampleData.TopicCounts);
        ReplaceSentimentBars();

        Rows.Clear();
        foreach (var row in SampleData.RecentRows)
            Rows.Add(row);

        TotalResponses = 137;
        NegativeDisplay = "12";
        AverageSentiment = "+0.42";
    }

    // Sample-only: drills into a single topic, showing a synthetic weekly breakdown.
    private void ShowTopic(string topic)
    {
        _drilledTopic = topic;
        CanDrillUp = true;
        Breadcrumb = $"{RangeLabel}  ＞  {topic}";
        LevelTitle = $"{topic} ・ 週別件数";

        var weekly = new (string Label, int Count)[]
        {
            ("第1週", 7),
            ("第2週", 12),
            ("第3週", 9),
            ("第4週", 6),
        };
        ReplaceBars(TopicBars, weekly);
        ReplaceSentimentBars();

        Rows.Clear();
        foreach (var row in SampleData.RecentRows)
            if (row.Topic == topic)
                Rows.Add(row);
        if (Rows.Count == 0)
            foreach (var row in SampleData.RecentRows)
                Rows.Add(row);

        TotalResponses = 34;
        NegativeDisplay = "4";
        AverageSentiment = "+0.18";
    }

    // クリックでトピックにドリルダウン（サンプルのみ）
    [RelayCommand]
    private void DrillInto(string? topic)
    {
        if (_isSample && !CanDrillUp && topic is not null)
            ShowTopic(topic);
    }

    // 集計に戻る（ドリルアップ）
    [RelayCommand]
    private void DrillUp() => ShowOverview();

    // Scales (label, count) pairs to pixel-width bars and replaces the target collection.
    private static void ReplaceBars(ObservableCollection<BarItem> target, IReadOnlyList<(string Label, int Count)> data)
    {
        var max = 1;
        foreach (var d in data)
            if (d.Count > max) max = d.Count;

        target.Clear();
        foreach (var d in data)
            target.Add(new BarItem { Label = d.Label, Count = d.Count, BarWidth = d.Count / (double)max * MaxBarWidth });
    }

    // Sentiment bars carry their own accent colors, so they get a dedicated builder.
    private void ReplaceSentimentBars()
    {
        var max = 1;
        foreach (var d in SampleData.SentimentCounts)
            if (d.Count > max) max = d.Count;

        SentimentBars.Clear();
        foreach (var d in SampleData.SentimentCounts)
            SentimentBars.Add(new BarItem { Label = d.Label, Count = d.Count, BarWidth = d.Count / (double)max * MaxBarWidth, Accent = d.Accent });
    }

    // ===== Date helpers =====

    private static readonly string[] DateFormats =
        { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyy年M月d日" };

    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string FormatDate(string? value) => TryParseDate(value, out var d) ? d.ToString("yyyy/MM/dd") : value ?? "—";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
