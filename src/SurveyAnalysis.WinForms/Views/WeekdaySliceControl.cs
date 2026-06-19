using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 時間別 → 曜日: a flat weekday table (月→日) within the 集計期間 window, every project field a column,
// drilling from a weekday into its 個票一覧. A single level, so the only navigation is the clickable
// breadcrumb (曜日 ＞ 月曜日) — no 戻る button and no level title. The shared SliceControlBase shell does
// the rest.
internal sealed class WeekdaySliceControl : SliceControlBase<WeekdaySliceViewModel>
{
    public WeekdaySliceControl(WeekdaySliceViewModel vm) : base(vm) { }

    protected override string HeaderTitle => "曜日";
    protected override string GridDimensionHeader => "曜日";
}
