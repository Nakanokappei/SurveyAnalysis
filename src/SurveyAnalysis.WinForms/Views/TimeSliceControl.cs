using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 時間別 → 期間: the drill-down analysis screen — the WinForms counterpart of the Avalonia 期間 view.
// A header (title + scope summary + 集計期間 picker), a breadcrumb with 戻る, the current level's title,
// and one card that shows either the clickable child breakdown (行=年度/月/週/日 × データ項目の列, 全体 行
// 付き) or — at the 日 terminal — the 個票一覧. Built from layout containers only (TableLayoutPanel rows,
// Dock/Anchor/AutoSize) per the layout rules; no explicit coordinates. The view model mutates its
// collections in place, so rather than subscribe to each change the control re-reads everything right
// after invoking a command (drill / 戻る / breadcrumb / period change), which all run synchronously.
internal sealed class TimeSliceControl : UserControl
{
    private readonly TimeSliceViewModel _vm;

    private readonly Label _scopeSummary = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0) };
    private readonly Label _levelTitle = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _empty = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly IconButton _drillUp = new() { Glyph = Icons.Back.Glyph, IconFontName = Icons.Back.Font, Text = "戻る" };
    private readonly FlowLayoutPanel _navBar = new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
    private readonly DataGridView _analysisGrid;
    private readonly DataGridView _responsesGrid = SliceTableView.BuildResponsesGrid();

    public TimeSliceControl(TimeSliceViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;
        _analysisGrid = SliceTableView.BuildAnalysisGrid("区分", vm.Columns);
        _analysisGrid.Cursor = Cursors.Hand; // rows drill down; the 全体 row (no Tag) is ignored on click

        BuildLayout();
        WireInteractions();
        RefreshAll();
    }

    // ===== Layout =====

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.ContentBack, Padding = new Padding(24) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, BuildHeader(), SizeType.AutoSize);
        AddRow(root, _navBar, SizeType.AutoSize);
        AddRow(root, _levelTitle, SizeType.AutoSize);
        AddRow(root, _empty, SizeType.AutoSize);
        AddRow(root, BuildTableCard(), SizeType.Percent);
        Controls.Add(root);
    }

    // Header: title + scope summary on the left, the 集計期間 picker on the right.
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Theme.ContentBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label { Text = "期間", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(_scopeSummary);

        header.Controls.Add(titles, 0, 0);
        // The picker pushes the new period into the view model (which reloads synchronously); refresh
        // the table afterwards. This handler runs after the picker's own value-push handler.
        var picker = SliceTableView.BuildPeriodPicker(_vm, out var periodCombo);
        periodCombo.SelectedIndexChanged += (_, _) => RefreshAll();
        header.Controls.Add(picker, 1, 0);
        return header;
    }

    // The single result card holds both grids; only the one for the current scope is visible.
    private Control BuildTableCard()
    {
        var card = SliceTableView.Card();
        card.MinimumSize = new Size(0, 240);
        _analysisGrid.Dock = DockStyle.Fill;
        _responsesGrid.Dock = DockStyle.Fill;
        card.Controls.Add(_analysisGrid);
        card.Controls.Add(_responsesGrid);
        return card;
    }

    private static void AddRow(TableLayoutPanel table, Control content, SizeType sizeType)
    {
        content.Margin = new Padding(0, 0, 0, 16);
        if (sizeType == SizeType.Percent)
            content.Dock = DockStyle.Fill;
        else
            content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.RowStyles.Add(new RowStyle(sizeType, sizeType == SizeType.Percent ? 100 : 0));
        table.Controls.Add(content, 0, table.RowCount);
        table.RowCount++;
    }

    // ===== Interaction =====

    private void WireInteractions()
    {
        // 戻る pops one drill level. Visibility tracks CanDrillUp; the layout itself never moves.
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

        // Clicking a child row drills into its scope (the source AnalysisRow is in the row Tag).
        _analysisGrid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _analysisGrid.Rows[e.RowIndex].Tag is AnalysisRow row)
            {
                _vm.DrillIntoCommand.Execute(row);
                RefreshAll();
            }
        };
    }

    // ===== Refresh =====

    // Re-reads the whole view model after any navigation: scope labels, the breadcrumb/戻る bar, which
    // grid is shown, and that grid's rows.
    private void RefreshAll()
    {
        _scopeSummary.Text = _vm.ScopeSummary;
        _levelTitle.Text = _vm.ChildLevelTitle;
        _empty.Text = _vm.EmptyMessage;
        _empty.Visible = !_vm.HasData;
        RebuildNavBar();

        _analysisGrid.Visible = _vm.ShowChildren;
        _responsesGrid.Visible = _vm.ShowResponses;
        if (_vm.ShowChildren)
        {
            _analysisGrid.Columns[0].HeaderText = _vm.ChildLevelTitle;
            SliceTableView.FillAnalysisGrid(_analysisGrid, _vm.Rows, _vm.HasTotal ? _vm.TotalRow : null);
        }
        if (_vm.ShowResponses)
            SliceTableView.FillResponsesGrid(_responsesGrid, _vm.Responses);
    }

    // Rebuilds the 戻る + breadcrumb row. The breadcrumb is regenerated from the current path each time;
    // the 戻る button is reused (re-parented) so its single Click handler is not re-added.
    private void RebuildNavBar()
    {
        _navBar.SuspendLayout();
        _navBar.Controls.Clear();
        if (_vm.CanDrillUp)
            _navBar.Controls.Add(_drillUp);
        _navBar.Controls.Add(SliceTableView.BuildBreadcrumb(_vm.Breadcrumbs, crumb =>
        {
            _vm.NavigateCrumbCommand.Execute(crumb);
            RefreshAll();
        }));
        _navBar.ResumeLayout();
    }
}
