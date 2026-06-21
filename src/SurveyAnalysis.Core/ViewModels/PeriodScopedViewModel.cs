using System;
using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Base for every 切り口 that carries the 対象期間 (date-range) picker shown top-right — the same picker the
// dashboard uses (DateRangeSelection). The range only narrows which responses are counted. Subclasses
// implement Reload to re-run their aggregation when the range changes.
public abstract class PeriodScopedViewModel : ViewModelBase
{
    private readonly DateRangeSelection _range = new();

    // 感情極性の推移：選択中の集計期間を月ごとに平均した折れ線データ。各 切り口 の上部に共通表示する。
    // 各サブクラスが Reload 時に AnalyticsRepository.SentimentTrend で詰める（ホストは再読込で読み直す）。
    public IReadOnlyList<SentimentTrendPoint> SentimentTrend { get; protected set; } = Array.Empty<SentimentTrendPoint>();

    // Current 対象期間 — read by the picker control to seed and label itself.
    public DateRangePreset Preset => _range.Preset;
    public DateTime From => _range.From;
    public DateTime To => _range.To;
    public string RangeLabel => _range.Label;

    // The [from, to] date_key window for the current selection.
    protected (long FromKey, long ToKey) Window => _range.DateKeyWindow;

    // Applies a range chosen in the picker, then re-runs the slice's aggregation.
    public void SetRange(DateRangePreset preset, DateTime from, DateTime to)
    {
        _range.Set(preset, from, to);
        Reload();
    }

    protected abstract void Reload();

    // The single 件数 column of a plain 軸 summary (時間別 / 曜日別 / 地域別 / トピック別 / 選択肢別): the
    // group's response count, shown beside the 感情極性 column.
    protected static readonly IReadOnlyList<AnalysisColumn> CountColumn =
        new[] { new AnalysisColumn("件数", FieldAggregation.Count) };

    // The analysis columns for a cross-tab: one 件数 column per category (in column order) then a 合計
    // 件数 column. The cell counts come from AnalyticsRepository.AggregateCrossTab.
    protected static IReadOnlyList<AnalysisColumn> CrossTabColumns(IReadOnlyList<string> categories) =>
        categories.Select(c => new AnalysisColumn(c, FieldAggregation.Count))
            .Append(new AnalysisColumn("合計", FieldAggregation.Count))
            .ToList();

    // The description under a cross-tab report's title (axis = 期間 / 曜日 / 地域).
    protected static string CrossTabDescription(string axis, CrossTabSpec spec) =>
        spec.Kind == CrossTabKind.Topic
            ? $"{axis}ごとに「{spec.Name}」のトピック別件数を集計します。行をクリックすると個票を表示します。"
            : $"{axis}ごとに「{spec.Name}」の選択肢別件数（複数選択は各オプションに計上）を集計します。行をクリックすると個票を表示します。";
}
