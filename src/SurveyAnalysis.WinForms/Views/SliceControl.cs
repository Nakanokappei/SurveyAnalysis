using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 地域別 / トピック別: a flat analysis table — one row per region/topic group within the 集計期間 window,
// a 感情極性 column plus every project field, and a trailing 全体 total row. Clicking a group drills into
// its 個票一覧 (記入日 / トピック / 感情 / 抜粋, PII hidden); a clickable breadcrumb (地域 ＞ 東京都) walks
// back. Built from layout containers only (no explicit coordinates); the view model mutates its
// collections in place, so the control re-reads everything right after invoking a command (drill /
// breadcrumb / period change), which all run synchronously — modelled on the time/weekday controls.
internal sealed class SliceControl : UserControl
{
    private readonly SliceViewModel _vm;

    private readonly Label _countSummary = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0) };
    private readonly Label _empty = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly FlowLayoutPanel _navBar = new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
    private readonly DataGridView _analysisGrid;
    private readonly DataGridView _responsesGrid = SliceTableView.BuildResponsesGrid();
    private readonly DateRangePicker _rangePicker = new();
    private readonly SentimentTrendChart _trendChart;
    private readonly Panel _trendCard;

    public SliceControl(SliceViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;
        _analysisGrid = SliceTableView.BuildAnalysisGrid(vm.DimensionTitle, vm.Columns);
        _analysisGrid.Cursor = Cursors.Hand; // rows drill to 個票; the 全体 row (no Tag) is ignored on click
        _trendCard = SliceTableView.BuildTrendCard(out _trendChart);
        _trendCard.MinimumSize = new Size(0, LogicalToDeviceUnits(176));

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
        AddRow(root, _trendCard, SizeType.AutoSize);
        AddRow(root, _navBar, SizeType.AutoSize);
        AddRow(root, _empty, SizeType.AutoSize);
        AddRow(root, BuildTableCard(), SizeType.Percent);
        Controls.Add(root);
    }

    // Header: title + description + 件数 summary on the left, the 集計期間 picker on the right.
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Theme.ContentBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label { Text = _vm.Title, AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(new Label { Text = _vm.Description, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0, 2, 0, 0) });
        titles.Controls.Add(_countSummary);

        header.Controls.Add(titles, 0, 0);
        // The picker pushes the new range into the view model (which reloads synchronously); RefreshAll
        // re-reads the table afterwards.
        header.Controls.Add(SliceTableView.BuildPeriodPicker(_vm, _rangePicker, RefreshAll), 1, 0);
        return header;
    }

    // The single result card holds both grids; only the one for the current level is visible.
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
        // Clicking a group row drills into its 個票 (the source AnalysisRow is in the row Tag).
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

    // Re-reads the view model after a period change / drill: 件数 summary, empty state, the trend, the
    // breadcrumb, which grid is shown, and that grid's rows.
    private void RefreshAll()
    {
        _countSummary.Text = _vm.CountSummary;
        _empty.Text = _vm.EmptyMessage;
        _empty.Visible = !_vm.HasData;
        _trendChart.SetData(_vm.SentimentTrend);
        _trendCard.Visible = _vm.SentimentTrend.Count > 0;
        RebuildNavBar();

        _analysisGrid.Visible = _vm.ShowRows;
        _responsesGrid.Visible = _vm.ShowResponses;
        if (_vm.ShowRows)
            SliceTableView.FillAnalysisGrid(_analysisGrid, _vm.Rows, _vm.HasTotal ? _vm.TotalRow : null);
        if (_vm.ShowResponses)
            SliceTableView.FillResponsesGrid(_responsesGrid, _vm.Responses);
    }

    // Rebuilds the breadcrumb row from the current path; clicking a non-current segment navigates back.
    private void RebuildNavBar()
    {
        _navBar.SuspendLayout();
        _navBar.Controls.Clear();
        _navBar.Controls.Add(SliceTableView.BuildBreadcrumb(_vm.Breadcrumbs, crumb =>
        {
            _vm.NavigateCrumbCommand.Execute(crumb);
            RefreshAll();
        }));
        _navBar.ResumeLayout();
    }
}
