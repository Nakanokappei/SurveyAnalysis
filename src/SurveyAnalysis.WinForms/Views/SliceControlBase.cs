using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The shared shell for every 切り口 (analysis-table) screen — 時間別(期間) / 時間別(曜日) / 地域別・トピック別.
// All three render the same parts in the same order: a header (title + summary + 集計期間 picker), the
// 感情極性の推移 trend card, a breadcrumb nav bar, an optional level title, and one result card holding the
// analysis grid and the 個票 grid (only one visible at a time). The differences are narrow and expressed as
// hooks: the header title/description, the leading dimension column header, an optional row above the empty
// state, breadcrumb-leading controls (時間別's 戻る), and per-refresh extras (時間別's level retitle).
//
// Built from layout containers only (TableLayoutPanel rows, Dock/Anchor/AutoSize) per the layout rules; no
// explicit coordinates. The view models mutate their collections in place, so the control re-reads the
// whole model right after invoking a command (drill / breadcrumb / period change), which all run
// synchronously — there is no per-property binding.
internal abstract class SliceControlBase<TVm> : UserControl where TVm : PeriodScopedViewModel, ISliceView
{
    protected readonly TVm _vm;

    private readonly Label _summary = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0) };
    private readonly Label _empty = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly FlowLayoutPanel _navBar = new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
    private readonly DataGridView _analysisGrid;
    private readonly DataGridView _responsesGrid = SliceTableView.BuildResponsesGrid();
    private readonly DateRangePicker _rangePicker = new();
    private readonly SentimentTrendChart _trendChart;
    private readonly Panel _trendCard;

    protected SliceControlBase(TVm vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;
        // GridDimensionHeader only reads _vm (set above) or a constant, so the virtual call in the ctor is
        // safe — no subclass field is read before it is initialized.
        _analysisGrid = SliceTableView.BuildAnalysisGrid(GridDimensionHeader, _vm.Columns);
        _analysisGrid.Cursor = Cursors.Hand; // rows drill down/into 個票; the 全体 row (no Tag) is ignored on click
        _trendCard = SliceTableView.BuildTrendCard(out _trendChart);
        _trendCard.MinimumSize = new Size(0, LogicalToDeviceUnits(176));

        BuildLayout();
        WireInteractions();
        RefreshAll();
    }

    // ===== Variation hooks =====

    // The big header title (左上). 地域別/トピック別 uses the report name; 時間別 uses 期間 / 曜日.
    protected abstract string HeaderTitle { get; }

    // An optional sub-title under the header title (地域別/トピック別's description); null = none.
    protected virtual string? HeaderDescription => null;

    // The leading (dimension) column header of the analysis grid: 地域 / トピック / 区分 / 曜日.
    protected abstract string GridDimensionHeader { get; }

    // An optional row inserted just above the empty-state line (時間別's drill-level title); null = none.
    protected virtual Control? AboveEmptyRow => null;

    // Controls placed before the breadcrumb in the nav bar (時間別's 戻る when it can pop a level).
    protected virtual IEnumerable<Control> NavBarLeading() => Array.Empty<Control>();

    // Subclass-specific interaction wiring (時間別 styles + wires its 戻る button here).
    protected virtual void OnWireInteractions() { }

    // Subclass-specific work on each refresh (時間別 retitles the level + the grid's first column).
    protected virtual void RefreshExtras() { }

    // 時間別 retitles the analysis grid's leading column per drill level; exposed for that override only.
    protected void SetDimensionColumnHeader(string text)
    {
        if (_analysisGrid.Columns.Count > 0)
            _analysisGrid.Columns[0].HeaderText = text;
    }

    // ===== Layout =====

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.ContentBack, Padding = new Padding(24) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, BuildHeader(), SizeType.AutoSize);
        AddRow(root, _trendCard, SizeType.AutoSize);
        AddRow(root, _navBar, SizeType.AutoSize);
        if (AboveEmptyRow is { } extra)
            AddRow(root, extra, SizeType.AutoSize);
        AddRow(root, _empty, SizeType.AutoSize);
        AddRow(root, BuildTableCard(), SizeType.Percent);
        Controls.Add(root);
    }

    // Header: title (+ optional description) + 件数/scope summary on the left, the 集計期間 picker on the right.
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Theme.ContentBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label { Text = HeaderTitle, AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(0) });
        if (HeaderDescription is { } description)
            titles.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0, 2, 0, 0) });
        titles.Controls.Add(_summary);

        header.Controls.Add(titles, 0, 0);

        // Right column: the 集計期間 picker with a CSV エクスポート button beneath it (both right-aligned). The
        // picker pushes the new range into the view model (which reloads synchronously); RefreshAll re-reads
        // the table afterwards.
        var rightStack = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, BackColor = Theme.ContentBack };
        rightStack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rightStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var picker = SliceTableView.BuildPeriodPicker(_vm, _rangePicker, RefreshAll);
        picker.Anchor = AnchorStyles.Right;
        rightStack.Controls.Add(picker, 0, 0);
        rightStack.Controls.Add(BuildCsvButton(), 0, 1);
        header.Controls.Add(rightStack, 1, 0);
        return header;
    }

    // The CSV エクスポート button under the 集計期間 picker: writes whichever grid is currently visible (the
    // analysis table, or the drilled 個票 list) to a user-chosen CSV — the data exactly as shown.
    private IconButton BuildCsvButton()
    {
        var button = new IconButton
        {
            Glyph = Icons.Csv.Glyph,
            IconFontName = Icons.Csv.Font,
            IconSize = 11f,
            Text = "CSV エクスポート",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.White,
            ForeColor = Theme.TitleText,
            Font = Theme.Font(9.5f),
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, LogicalToDeviceUnits(6), 0, 0),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Theme.CardBorder;
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => CsvExport.Export(this, _analysisGrid.Visible ? _analysisGrid : _responsesGrid, HeaderTitle);
        return button;
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
        // Clicking a group/period row drills into it (the source AnalysisRow is in the row Tag).
        _analysisGrid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _analysisGrid.Rows[e.RowIndex].Tag is AnalysisRow row)
            {
                _vm.DrillIntoCommand.Execute(row);
                RefreshAll();
            }
        };
        // Clicking a trend marker narrows the 集計期間 to that day / week; the report (table + trend)
        // re-aggregates and the period picker reflects the new range.
        _trendChart.PointClicked += (_, point) =>
        {
            _vm.SetRange(DateRangePreset.Custom, point.From, point.To);
            _rangePicker.SetCurrent(_vm.Preset, _vm.From, _vm.To);
            RefreshAll();
        };
        OnWireInteractions();
    }

    // ===== Refresh =====

    // Re-reads the whole view model after any navigation: summary, empty state, trend, the breadcrumb,
    // which grid is shown, and that grid's rows.
    protected void RefreshAll()
    {
        _summary.Text = _vm.Summary;
        _empty.Text = _vm.EmptyMessage;
        _empty.Visible = !_vm.HasData;
        _trendChart.SetData(_vm.SentimentTrend);
        _trendCard.Visible = _vm.SentimentTrend.Count > 0;
        RefreshExtras();
        RebuildNavBar();

        _analysisGrid.Visible = _vm.ShowRows;
        _responsesGrid.Visible = _vm.ShowResponses;
        if (_vm.ShowRows)
            SliceTableView.FillAnalysisGrid(_analysisGrid, _vm.Rows, _vm.HasTotal ? _vm.TotalRow : null);
        if (_vm.ShowResponses)
            SliceTableView.FillResponsesGrid(_responsesGrid, _vm.Responses);
    }

    // Rebuilds the nav bar: any leading controls (時間別's 戻る) then the breadcrumb regenerated from the
    // current path. Leading controls are reused (re-parented), so their handlers are not re-added.
    private void RebuildNavBar()
    {
        _navBar.SuspendLayout();
        _navBar.Controls.Clear();
        foreach (var leading in NavBarLeading())
            _navBar.Controls.Add(leading);
        _navBar.Controls.Add(SliceTableView.BuildBreadcrumb(_vm.Breadcrumbs, crumb =>
        {
            _vm.NavigateCrumbCommand.Execute(crumb);
            RefreshAll();
        }));
        _navBar.ResumeLayout();
    }
}
