using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The dashboard shown in the main pane: KPIs, a topic bar chart, a sentiment bar chart, and a
// table — for the selected 対象期間. Saved projects aggregate their real imported responses through the
// analytics star (the same source the slices use, so the two never diverge); the bundled sample
// project (not persisted, Id 0) keeps the illustrative dummy data so the full layout stays reviewable.
// Topic and sentiment need LLM analysis (not yet implemented), so for real projects those are shown as
// pending; total responses and the answer list are real.
public partial class DashboardViewModel : ViewModelBase
{
    private const double MaxBarWidth = 180;

    private readonly bool _isSample;
    private readonly long _projectId;
    private readonly AnalyticsRepository _analytics;
    private readonly string? _aggregationFieldName;
    private readonly string? _excerptFieldName;
    private string? _drilledTopic;

    // The active 対象期間 — the same selection model the slices use (default 直近30日).
    private readonly DateRangeSelection _range = new();
    public DateRangePreset Preset => _range.Preset;
    public DateTime From => _range.From;
    public DateTime To => _range.To;
    public string RangeLabel => _range.Label;

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

    // The "click a bar to drill down" hint only applies to the sample's clickable chart (real-project
    // topic bars are not drillable).
    public bool ShowDrillHint => _isSample && !CanDrillUp && !AnalysisPending;

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

    public DashboardViewModel(Project project, AnalyticsRepository analytics)
    {
        _projectId = project.Id;
        _analytics = analytics;

        // The aggregation date field gives each response its 記入日 and decides whether it falls in the
        // selected range; the excerpt comes from a free-text (or sentiment) field — never from PII.
        _aggregationFieldName = project.Fields.FirstOrDefault(f => f.UseForAggregation)?.Name;
        _excerptFieldName =
            project.Fields.FirstOrDefault(f => f.FieldType == FieldType.FreeText)?.Name
            ?? project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Sentiment)?.Name;

        _isSample = project.Id == 0;
        // Keep the star current so the dashboard reflects the latest import (and survives the startup
        // rebuild of the derived tables); the sample project is not persisted, so it stays on dummy data.
        if (!_isSample)
            _analytics.Rebuild(project);

        ShowOverview();
    }

    // Applies a new 対象期間 chosen in the picker, then rebuilds the overview. Custom carries the user's
    // [from, to]; a preset's range is recomputed by the caller (the picker) so it stays anchored at today.
    public void SetRange(DateRangePreset preset, DateTime from, DateTime to)
    {
        _range.Set(preset, from, to);
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

    // Real overview: pulls each response in the selected 対象期間 with its persisted analysis (sentiment +
    // main topic) from the star — the same source the slices read. KPIs, the topic / sentiment charts and
    // the per-response 感情 / トピック columns are all derived from it. When responses exist but none have
    // been analysed yet (no API key / analysis not run), the charts stay empty and pending is shown.
    private void ShowRealOverview()
    {
        var (fromKey, toKey) = _range.DateKeyWindow;
        var inRange = _analytics.ResponsesWithAnalysisForScope(_projectId, fromKey, toKey, newestFirst: true);
        TotalResponses = inRange.Count;
        HasNoResponses = inRange.Count == 0;

        var analysed = inRange.Any(r => r.SentimentScore is not null || r.Topic is not null);
        AnalysisPending = inRange.Count > 0 && !analysed;

        if (analysed)
        {
            var scores = inRange.Where(r => r.SentimentScore is not null).Select(r => r.SentimentScore!.Value).ToList();
            AverageSentiment = scores.Count == 0 ? "—" : scores.Average().ToString("+0.00;-0.00;0.00");
            NegativeDisplay = inRange.Count(r => r.IsNegative).ToString();
            ReplaceBars(TopicBars, TopicCounts(inRange));
            ReplaceSentimentBars(SentimentDistribution(inRange));
        }
        else
        {
            AverageSentiment = "—";
            NegativeDisplay = "—";
            TopicBars.Clear();
            SentimentBars.Clear();
        }

        Rows.Clear();
        foreach (var response in inRange)
            Rows.Add(BuildRow(response));
    }

    // Response counts per assigned (main 自由記述) topic, largest first; responses with no topic are left
    // out. Same grouping the トピック別 slice uses (main_topic_key), so the two never disagree.
    private static IReadOnlyList<(string Label, int Count)> TopicCounts(IReadOnlyList<ResponseAnalysis> responses)
    {
        var counts = new Dictionary<string, int>();
        foreach (var response in responses)
            if (!string.IsNullOrEmpty(response.Topic))
                counts[response.Topic] = counts.GetValueOrDefault(response.Topic) + 1;
        return counts.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).ToList();
    }

    // The three-way sentiment split (colours match the sample): ネガティブ is the LLM's negative flag, and
    // the remaining responses divide into ポジティブ (a clearly positive score) and 中立. Unscored
    // responses are excluded.
    private static IReadOnlyList<(string Label, int Count, string Accent)> SentimentDistribution(IReadOnlyList<ResponseAnalysis> responses)
    {
        int positive = 0, neutral = 0, negative = 0;
        foreach (var response in responses)
            if (response.IsNegative)
                negative++;
            else if (response.SentimentScore is { } score && score >= 0.2)
                positive++;
            else if (response.SentimentScore is not null)
                neutral++;
        return new[]
        {
            ("ポジティブ", positive, "#16A34A"),
            ("中立", neutral, "#CA8A04"),
            ("ネガティブ", negative, "#DC2626"),
        };
    }

    // Builds one table row from a response: 記入日 from the aggregation date, the excerpt from the
    // free-text field, the assigned topic and the row sentiment score. Personal information is never shown.
    private SurveyRow BuildRow(ResponseAnalysis response)
    {
        var entryDate = "—";
        if (_aggregationFieldName is not null && response.Values.TryGetValue(_aggregationFieldName, out var date))
            entryDate = FormatDate(date);

        var excerpt = "";
        if (_excerptFieldName is not null && response.Values.TryGetValue(_excerptFieldName, out var text))
            excerpt = Truncate(text, 40);

        var sentiment = response.SentimentScore is { } score ? score.ToString("+0.00;-0.00;0.00") : "—";
        return new SurveyRow { EntryDate = entryDate, Topic = response.Topic ?? "—", Sentiment = sentiment, Excerpt = excerpt };
    }

    // ===== Sample (dummy) dashboard, preserved for layout review =====

    private void ShowSampleOverview()
    {
        AnalysisPending = false;
        HasNoResponses = false;

        ReplaceBars(TopicBars, SampleData.TopicCounts);
        ReplaceSentimentBars(SampleData.SentimentCounts);

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
        ReplaceSentimentBars(SampleData.SentimentCounts);

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
    private void ReplaceSentimentBars(IReadOnlyList<(string Label, int Count, string Accent)> data)
    {
        var max = 1;
        foreach (var d in data)
            if (d.Count > max) max = d.Count;

        SentimentBars.Clear();
        foreach (var d in data)
            SentimentBars.Add(new BarItem { Label = d.Label, Count = d.Count, BarWidth = d.Count / (double)max * MaxBarWidth, Accent = d.Accent });
    }

    // ===== Date helpers =====

    private static string FormatDate(string? value) => DateParsing.TryParse(value, out var d) ? d.ToString("yyyy/MM/dd") : value ?? "—";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
