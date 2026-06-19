using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The surface every 切り口 view model exposes so a single SliceControlBase can render and drive it.
// 時間別(期間) / 時間別(曜日) / 地域別・トピック別 each map their own members onto this — e.g. TimeSlice's
// ScopeSummary → Summary and ShowChildren → ShowRows — so the table, breadcrumb, trend chart and 個票
// drill are built once. Implemented alongside PeriodScopedViewModel (which carries the 集計期間 picker).
public interface ISliceView
{
    // The analysis table: one column per project field (plus a 感情極性 column), one row per group in the
    // slice dimension, with a trailing 全体 total row.
    IReadOnlyList<AnalysisColumn> Columns { get; }
    ObservableCollection<AnalysisRow> Rows { get; }
    AnalysisRow? TotalRow { get; }
    bool HasTotal { get; }

    // The drilled 個票一覧 (記入日 / トピック / 感情 / 抜粋), shown instead of the table at the terminal level.
    ObservableCollection<SurveyRow> Responses { get; }

    // The clickable path back up (地域 ＞ 東京都 等).
    ObservableCollection<Crumb> Breadcrumbs { get; }

    // 感情極性の推移（集計期間を月ごとに平均した折れ線）。各レポート上部に共通表示する。
    IReadOnlyList<SentimentTrendPoint> SentimentTrend { get; }

    bool HasData { get; }
    bool ShowRows { get; }       // the analysis table is the visible grid
    bool ShowResponses { get; }  // the 個票 list is the visible grid
    string EmptyMessage { get; }
    string Summary { get; }      // the 件数 / scope summary line under the title

    // Drill a clicked group/period row into its 個票; walk back to a clicked breadcrumb segment.
    ICommand DrillIntoCommand { get; }
    ICommand NavigateCrumbCommand { get; }
}
