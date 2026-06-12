using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// One analytics "切り口" (slice): the project's responses grouped by a single dimension — time,
// region, or topic — and counted. It refreshes the star schema (ETL) on open, then runs the slice
// query. The time slice adds a grain selector (年度 / 四半期 / 月 / 週 / 曜日); region and topic are a
// single grouping. When a dimension cannot be shown (no field for it, or topic before LLM), it
// explains why instead of charting an empty result.
public partial class SliceViewModel : ViewModelBase
{
    private const double MaxBarWidth = 220;

    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;

    public string Title { get; }
    public string Description { get; }

    // The grouped bars (dimension value → count), or empty when there is nothing to show.
    public ObservableCollection<BarItem> Bars { get; } = new();

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _emptyMessage = "";

    [ObservableProperty]
    private string _countSummary = "";

    // Time-grain selector (only the time slice shows it).
    public bool ShowGrainSelector { get; }
    public ObservableCollection<TimeGrain> Grains { get; } = new();

    [ObservableProperty]
    private TimeGrain _selectedGrain = TimeGrain.Month;

    public SliceViewModel(Project project, AnalyticsRepository analytics, SliceKind kind)
    {
        _analytics = analytics;
        _projectId = project.Id;

        Title = SliceInfo.Label(kind);
        Description = kind switch
        {
            SliceKind.Time => "回答を時間軸（年度・四半期・月・週・曜日）で集計します。",
            SliceKind.Region => "回答を地域（住所・都道府県・市区町村）ごとに集計します。",
            SliceKind.Topic => "回答をトピックごとに集計します。",
            _ => ""
        };

        // Keep the star current with the latest imported responses.
        analytics.Rebuild(project);

        // Topic needs LLM topic assignment regardless of whether a topic field exists.
        if (kind == SliceKind.Topic)
        {
            EmptyMessage = "トピック分析は LLM 連携後に表示されます。";
            return;
        }

        // Without the field that feeds this dimension, there is nothing to group by.
        var fieldName = kind == SliceKind.Time
            ? AnalyticsRepository.DateField(project)
            : AnalyticsRepository.RegionField(project);
        if (fieldName is null)
        {
            EmptyMessage = kind == SliceKind.Time
                ? "このプロジェクトには月次集計の基準日（日付項目）がありません。「データ項目」で設定できます。"
                : "このプロジェクトには地域項目（住所・都道府県・市区町村）がありません。「データ項目」で追加できます。";
            return;
        }

        if (kind == SliceKind.Time)
        {
            ShowGrainSelector = true;
            foreach (var grain in new[] { TimeGrain.FiscalYear, TimeGrain.FiscalQuarter, TimeGrain.Month, TimeGrain.Week, TimeGrain.DayOfWeek })
                Grains.Add(grain);
            LoadTime(); // uses SelectedGrain (defaults to 月別)
        }
        else
        {
            Load(analytics.AggregateBy(_projectId, kind));
        }
    }

    // Re-aggregate when the user switches the time grain.
    partial void OnSelectedGrainChanged(TimeGrain value) => LoadTime();

    private void LoadTime() => Load(_analytics.AggregateByTime(_projectId, SelectedGrain));

    // Turns aggregation groups into scaled bars, or sets the empty-state message when there is none.
    private void Load(IReadOnlyList<(string Label, int Count)> groups)
    {
        Bars.Clear();
        var total = groups.Sum(g => g.Count);
        if (groups.Count == 0 || total == 0)
        {
            HasData = false;
            CountSummary = "";
            EmptyMessage = "まだ回答がありません。サイドバーの「インポート (CSV)」から取り込めます。";
            return;
        }

        var max = Math.Max(1, groups.Max(g => g.Count));
        foreach (var (label, count) in groups)
            Bars.Add(new BarItem { Label = label, Count = count, BarWidth = count / (double)max * MaxBarWidth });

        CountSummary = $"合計 {total} 件 ・ {groups.Count} グループ";
        EmptyMessage = "";
        HasData = true;
    }
}
