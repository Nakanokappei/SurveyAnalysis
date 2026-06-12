using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// 地域別 / トピック別: a flat analysis table grouping responses by region or topic within the 集計期間
// window. Every project field is a column, aggregated by its type (種類数 / 合計 / 平均). Region groups by
// 都道府県 etc. (missing → （未設定）); topic groups by LLM topic (none yet → （未分析）). 時間別 is its own
// drill-down view (TimeSliceViewModel / WeekdaySliceViewModel).
public partial class SliceViewModel : PeriodScopedViewModel
{
    private readonly AnalyticsRepository _analytics;
    private readonly long _projectId;
    private readonly AnalysisGrouping _grouping;

    public string Title { get; }
    public string Description { get; }
    // The leading (dimension) column heading: 地域 or トピック.
    public string DimensionTitle { get; }

    public IReadOnlyList<AnalysisColumn> Columns { get; }
    public ObservableCollection<AnalysisRow> Rows { get; } = new();

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _emptyMessage = "";

    [ObservableProperty]
    private string _countSummary = "";

    public SliceViewModel(Project project, AnalyticsRepository analytics, SliceKind kind)
    {
        _analytics = analytics;
        _projectId = project.Id;
        _grouping = kind == SliceKind.Topic ? AnalysisGrouping.Topic : AnalysisGrouping.Region;
        DimensionTitle = kind == SliceKind.Topic ? "トピック" : "地域";

        Title = SliceInfo.Label(kind);
        Description = kind switch
        {
            SliceKind.Region => "回答を地域（住所・都道府県・市区町村）ごとに集計します。",
            SliceKind.Topic => "回答をトピックごとに集計します。",
            _ => ""
        };

        Columns = project.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new AnalysisColumn(f.Name, FieldAggregationInfo.For(f)))
            .ToList();

        // Keep the star current with the latest imported responses.
        analytics.Rebuild(project);

        Reload();
    }

    // Re-aggregate within the selected 集計期間 window.
    protected override void Reload()
    {
        var (from, to) = Window;
        var rows = _analytics.AggregateRows(_projectId, _grouping, TimeScope.Root, from, to, Columns);

        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);

        var total = rows.Sum(r => r.Count);
        CountSummary = $"合計 {total} 件 ・ {rows.Count} グループ";
        HasData = rows.Count > 0;
        EmptyMessage = HasData ? "" : "この集計期間には回答がありません。右上の集計期間を広げてください。";
    }
}
