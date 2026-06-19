using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 地域別 / トピック別: a flat analysis table — one row per region/topic group within the 集計期間 window,
// a 感情極性 column plus every project field, and a trailing 全体 total row. Clicking a group drills into
// its 個票一覧; a clickable breadcrumb (地域 ＞ 東京都) walks back. The report name and a description come
// from the view model; everything else is the shared SliceControlBase shell.
internal sealed class SliceControl : SliceControlBase<SliceViewModel>
{
    public SliceControl(SliceViewModel vm) : base(vm) { }

    protected override string HeaderTitle => _vm.Title;
    protected override string? HeaderDescription => _vm.Description;
    protected override string GridDimensionHeader => _vm.DimensionTitle;
}
