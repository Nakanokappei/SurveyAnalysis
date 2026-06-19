using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 時間別 → 期間: the drill-down analysis screen. Start at 全期間 grouped by 年度, click into 月別 / 週別 /
// 日別, and at the 日 terminal see the 個票一覧. Unlike the flat slices it adds two parts to the shared
// shell: a 戻る button before the breadcrumb (when a level can be popped) and a drill-level title above the
// table, and it retitles the grid's leading column per level. Everything else is SliceControlBase.
internal sealed class TimeSliceControl : SliceControlBase<TimeSliceViewModel>
{
    private readonly Label _levelTitle = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
    private readonly IconButton _drillUp = new() { Glyph = Icons.Back.Glyph, IconFontName = Icons.Back.Font, Text = "戻る" };

    public TimeSliceControl(TimeSliceViewModel vm) : base(vm) { }

    protected override string HeaderTitle => "期間";
    protected override string GridDimensionHeader => "区分";
    protected override Control? AboveEmptyRow => _levelTitle;

    // 戻る is styled once and shown only when there is a level to pop (NavBarLeading).
    protected override void OnWireInteractions()
    {
        _drillUp.AutoSize = true;
        _drillUp.Anchor = AnchorStyles.Left;
        _drillUp.BackColor = Theme.CardBorder;
        _drillUp.ForeColor = Theme.TitleText;
        _drillUp.Font = Theme.Font(9.5f);
        _drillUp.Padding = new Padding(12, 6, 12, 6);
        _drillUp.FlatAppearance.BorderSize = 0;
        _drillUp.Cursor = Cursors.Hand;
        _drillUp.Margin = new Padding(0, 0, 12, 0);
        _drillUp.Click += (_, _) => { _vm.DrillUpCommand.Execute(null); RefreshAll(); };
    }

    protected override IEnumerable<Control> NavBarLeading() =>
        _vm.CanDrillUp ? new Control[] { _drillUp } : Array.Empty<Control>();

    // The drill level names both the section title above the table and the table's first-column header.
    protected override void RefreshExtras()
    {
        _levelTitle.Text = _vm.ChildLevelTitle;
        if (_vm.ShowChildren)
            SetDimensionColumnHeader(_vm.ChildLevelTitle);
    }
}
