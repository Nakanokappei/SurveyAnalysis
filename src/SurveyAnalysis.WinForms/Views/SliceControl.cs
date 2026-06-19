using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// 地域別 / トピック別: a flat analysis table — one row per region/topic group within the 集計期間 window,
// every project field a column (aggregated by its type 種類数 / 合計 / 平均), with a trailing 全体 total
// row. SliceViewModel exposes no child levels, so there is no drill-down, breadcrumb, or 個票 list — only
// the 集計期間 picker re-aggregates. Built from layout containers only (no explicit coordinates); the view
// model mutates Rows in place when the period changes, so the control re-reads after the picker pushes it.
internal sealed class SliceControl : UserControl
{
    private readonly SliceViewModel _vm;

    private readonly Label _countSummary = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0) };
    private readonly Label _empty = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly DataGridView _analysisGrid;
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
        _trendCard = SliceTableView.BuildTrendCard(out _trendChart);
        _trendCard.MinimumSize = new Size(0, LogicalToDeviceUnits(176));

        BuildLayout();
        RefreshAll();
    }

    // ===== Layout =====

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.ContentBack, Padding = new Padding(24) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, BuildHeader(), SizeType.AutoSize);
        AddRow(root, _trendCard, SizeType.AutoSize);
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

    private Control BuildTableCard()
    {
        var card = SliceTableView.Card();
        card.MinimumSize = new Size(0, 240);
        _analysisGrid.Dock = DockStyle.Fill;
        card.Controls.Add(_analysisGrid);
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

    // ===== Refresh =====

    // Re-reads the view model after a period change: 件数 summary, empty state, and the table rows.
    private void RefreshAll()
    {
        _countSummary.Text = _vm.CountSummary;
        _empty.Text = _vm.EmptyMessage;
        _empty.Visible = !_vm.HasData;
        _trendChart.SetData(_vm.SentimentTrend);
        _trendCard.Visible = _vm.SentimentTrend.Count > 0;
        _analysisGrid.Visible = _vm.HasData;
        if (_vm.HasData)
            SliceTableView.FillAnalysisGrid(_analysisGrid, _vm.Rows, _vm.HasTotal ? _vm.TotalRow : null);
    }
}
