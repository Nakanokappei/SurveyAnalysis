using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The dashboard shown in the main pane: KPIs, a topic bar chart, a sentiment bar chart,
// and a table — all for the selected month. Supports one level of drill-down/up: clicking
// a topic bar drills into that topic's weekly breakdown; 集計に戻る drills back up.
public partial class DashboardViewModel : ViewModelBase
{
    private const double MaxBarWidth = 180;

    private readonly Project _project;
    private string? _drilledTopic;

    // Months available for selection in the dashboard header.
    public ObservableCollection<string> Months { get; }

    [ObservableProperty]
    private string _month;

    // Breadcrumb shown above the charts, e.g. "2026年5月" or "2026年5月 ＞ 配線・接続".
    [ObservableProperty]
    private string _breadcrumb = "";

    [ObservableProperty]
    private string _levelTitle = "";

    // True only when drilled into a topic; shows the 集計に戻る (drill-up) button.
    [ObservableProperty]
    private bool _canDrillUp;

    // KPI cards.
    [ObservableProperty]
    private int _totalResponses;

    [ObservableProperty]
    private int _negativeCount;

    [ObservableProperty]
    private string _averageSentiment = "";

    public ObservableCollection<BarItem> TopicBars { get; } = new();
    public ObservableCollection<BarItem> SentimentBars { get; } = new();
    public ObservableCollection<SurveyRow> Rows { get; } = new();

    public DashboardViewModel(Project project, string month)
    {
        _project = project;
        Months = project.Months;
        _month = month;
        ShowOverview();
    }

    // Re-selecting a month from the header resets to the overview level.
    partial void OnMonthChanged(string value) => DrillUp();

    // Builds the month overview: topic + sentiment distribution and recent rows.
    private void ShowOverview()
    {
        _drilledTopic = null;
        CanDrillUp = false;
        Breadcrumb = Month;
        LevelTitle = "トピック別 件数";

        ReplaceBars(TopicBars, SampleData.TopicCounts);
        ReplaceSentimentBars();

        Rows.Clear();
        foreach (var row in SampleData.RecentRows)
            Rows.Add(row);

        TotalResponses = 137;
        NegativeCount = 12;
        AverageSentiment = "+0.42";
    }

    // Drills into a single topic, showing a synthetic weekly breakdown and filtered rows.
    private void ShowTopic(string topic)
    {
        _drilledTopic = topic;
        CanDrillUp = true;
        Breadcrumb = $"{Month}  ＞  {topic}";
        LevelTitle = $"{topic} ・ 週別件数";

        // Synthetic weekly split of the topic's total, for layout demonstration.
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
        // If the topic has no sample excerpts, fall back to showing all so the table isn't empty.
        if (Rows.Count == 0)
            foreach (var row in SampleData.RecentRows)
                Rows.Add(row);

        TotalResponses = 34;
        NegativeCount = 4;
        AverageSentiment = "+0.18";
    }

    // クリックでトピックにドリルダウン
    [RelayCommand]
    private void DrillInto(string? topic)
    {
        if (!CanDrillUp && topic is not null)
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
}
