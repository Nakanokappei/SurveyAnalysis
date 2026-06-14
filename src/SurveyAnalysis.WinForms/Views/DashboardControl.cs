using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The dashboard — the WinForms counterpart of DashboardView.axaml. Header (title + breadcrumb +
// month picker), an optional drill-up button, three KPI cards, two bar charts (topic / sentiment),
// and the responses table. Built entirely from layout containers (TableLayoutPanel rows/columns,
// Dock/Anchor/AutoSize, Margin/Padding) — no explicit coordinates or sizes — so alignment, spacing,
// width-fill, and DPI scaling are all handled by the framework. Binds to DashboardViewModel: scalar
// labels and visibility refresh on PropertyChanged; charts and table rebuild on collection changes.
internal sealed class DashboardControl : UserControl
{
    private readonly DashboardViewModel _vm;

    private readonly DateRangePicker _rangePicker = new();
    private readonly Label _breadcrumb = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0) };
    private readonly IconButton _drillUp = new() { Glyph = Icons.Back.Glyph, IconFontName = Icons.Back.Font, Text = "集計に戻る" };
    private readonly Label _totalResponses = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(22f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _negative = new() { AutoSize = true, ForeColor = Theme.Danger, Font = Theme.Font(22f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _avgSentiment = new() { AutoSize = true, ForeColor = Theme.Success, Font = Theme.Font(22f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _topicTitle = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _topicHint = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _topicPending = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6), Text = "トピック分析は LLM 連携後に表示されます。" };
    private readonly TableLayoutPanel _topicBars = NewBarsPanel();
    private readonly Label _sentimentPending = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6), Text = "感情分析は LLM 連携後に表示されます。" };
    private readonly TableLayoutPanel _sentimentBars = NewBarsPanel();
    private readonly Label _emptyHint = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 6), Text = "まだ回答がありません。サイドバーの「インポート (CSV)」から取り込めます。" };
    private readonly DataGridView _grid = NewGrid();

    public DashboardControl(DashboardViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;
        BuildLayout();

        RefreshScalars();
        RefreshBars(_topicBars, _vm.TopicBars, drillable: true);
        RefreshBars(_sentimentBars, _vm.SentimentBars, drillable: false);
        RefreshTable();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.TopicBars.CollectionChanged += OnTopicBarsChanged;
        _vm.SentimentBars.CollectionChanged += OnSentimentBarsChanged;
        _vm.Rows.CollectionChanged += OnRowsChanged;
        _rangePicker.SetCurrent(_vm.Preset, _vm.From, _vm.To);
        _rangePicker.RangeChanged += (preset, from, to) => _vm.SetRange(preset, from, to);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.TopicBars.CollectionChanged -= OnTopicBarsChanged;
            _vm.SentimentBars.CollectionChanged -= OnSentimentBarsChanged;
            _vm.Rows.CollectionChanged -= OnRowsChanged;
        }
        base.Dispose(disposing);
    }

    // ===== Layout =====

    private void BuildLayout()
    {
        // One column; the table row takes the remaining height (its grid scrolls internally), the rest
        // size to their content. Dock=Fill children stretch to the column width — no manual width sync.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.ContentBack, Padding = new Padding(24) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, BuildHeader(), SizeType.AutoSize);
        AddRow(root, BuildDrillUp(), SizeType.AutoSize);
        AddRow(root, BuildKpis(), SizeType.AutoSize);
        AddRow(root, BuildCharts(), SizeType.AutoSize);
        AddRow(root, BuildTable(), SizeType.Percent);
        Controls.Add(root);
    }

    // Adds a top-level section row with a gap below it.
    private static void AddRow(TableLayoutPanel table, Control content, SizeType sizeType)
    {
        content.Margin = new Padding(0, 0, 0, 16);
        AddInner(table, content, sizeType);
    }

    // Adds a row to a stack. An auto-sized row's child must report its own height (so it must be
    // AutoSize) and only stretch horizontally via Anchor — using Dock=Fill there would collapse the
    // row. A percent row's child fills the known cell height with Dock=Fill.
    private static void AddInner(TableLayoutPanel table, Control content, SizeType sizeType)
    {
        if (sizeType == SizeType.Percent)
            content.Dock = DockStyle.Fill;
        else
            content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.RowStyles.Add(new RowStyle(sizeType, sizeType == SizeType.Percent ? 100 : 0));
        table.Controls.Add(content, 0, table.RowCount);
        table.RowCount++;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Theme.ContentBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label { Text = "ダッシュボード", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(_breadcrumb);

        var picker = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right };
        picker.Controls.Add(new Label { Text = "対象期間", AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(10f), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 8, 0) });
        picker.Controls.Add(_rangePicker.Trigger);

        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(picker, 1, 0);
        return header;
    }

    private Control BuildDrillUp()
    {
        _drillUp.AutoSize = true;
        _drillUp.Anchor = AnchorStyles.Left;
        _drillUp.BackColor = Theme.CardBorder;
        _drillUp.ForeColor = Theme.TitleText;
        _drillUp.Font = Theme.Font(9.5f);
        _drillUp.Padding = new Padding(14, 7, 14, 7);
        _drillUp.FlatAppearance.BorderSize = 0;
        _drillUp.Cursor = Cursors.Hand;
        _drillUp.Margin = new Padding(0);
        _drillUp.Click += (_, _) => _vm.DrillUpCommand.Execute(null);
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.ContentBack };
        row.Controls.Add(_drillUp);
        return row;
    }

    // Three equal KPI cards in a row.
    private Control BuildKpis()
    {
        var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 3; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        grid.Controls.Add(KpiCard("総回答数", _totalResponses, "件", last: false), 0, 0);
        grid.Controls.Add(KpiCard("ネガティブ件数", _negative, "要対応 件数", last: false), 1, 0);
        grid.Controls.Add(KpiCard("平均感情スコア", _avgSentiment, "-1.0 〜 +1.0", last: true), 2, 0);
        return grid;
    }

    // A KPI card: title / large value / subtitle. AutoSize (so the row sizes to it) + Anchor to fill
    // the column width without collapsing.
    private static Control KpiCard(string title, Label value, string subtitle, bool last)
    {
        var card = Card();
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.Margin = new Padding(0, 0, last ? 0 : 16, 0);
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = Color.White };
        stack.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Margin = new Padding(0, 0, 0, 2) });
        stack.Controls.Add(value);
        stack.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(8.5f), Margin = new Padding(0) });
        card.Controls.Add(stack);
        return card;
    }

    // Two chart cards: topic (clickable bars) and sentiment. Cards AutoSize to their content (with a
    // minimum height) and Anchor to fill their column.
    private Control BuildCharts()
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        grid.Controls.Add(ChartCard(new Padding(0, 0, 9, 0), _topicTitle, _topicHint, _topicPending, _topicBars), 0, 0);

        var sentimentTitle = new Label { Text = "感情極性の分布", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
        grid.Controls.Add(ChartCard(new Padding(9, 0, 0, 0), sentimentTitle, _sentimentPending, _sentimentBars), 1, 0);
        return grid;
    }

    // A chart card: a stack of label rows plus the bars, AutoSize with a minimum height.
    private static Control ChartCard(Padding margin, params Control[] rows)
    {
        var card = Card();
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.Margin = margin;
        card.MinimumSize = new Size(0, 220);
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows)
            AddInner(stack, row, SizeType.AutoSize);
        card.Controls.Add(stack);
        return card;
    }

    private Control BuildTable()
    {
        var card = Card();
        card.MinimumSize = new Size(0, 200);
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.White };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddInner(stack, new Label { Text = "回答一覧（抜粋）", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) }, SizeType.AutoSize);
        AddInner(stack, _emptyHint, SizeType.AutoSize);
        AddInner(stack, _grid, SizeType.Percent);
        card.Controls.Add(stack);
        return card;
    }

    // ===== Builders =====

    // A white card with a soft 1px border (softer than BorderStyle.FixedSingle's hard line).
    private static Panel Card()
    {
        var panel = new Panel { BackColor = Color.White, Padding = new Padding(16) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.CardBorder);
            var r = panel.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        };
        return panel;
    }

    // A bars container: one column, one auto-sized row per bar; the panel grows with its bars.
    private static TableLayoutPanel NewBarsPanel()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private static DataGridView NewGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            // No gridlines between cells — a flat list, matching the welcome screen's project table.
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            Font = Theme.Font(9.5f),
            EnableHeadersVisualStyles = false,
        };
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Font(9.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        // Soft row-selection highlight (not the heavy system blue), like the welcome table.
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = Theme.TitleText;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xD6, 0xE6, 0xF5);
        grid.DefaultCellStyle.SelectionForeColor = Theme.TitleText;
        // No header highlight for the selected column: keep the selected-state header colours the same as
        // the normal ones (otherwise FullRowSelect tints the current column's header).
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "記入日", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "トピック", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "感情", FillWeight = 14 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "抜粋（フリーテキスト）", FillWeight = 42 });
        // Proportional column widths that adapt to any width (no fixed pixels).
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        return grid;
    }

    // ===== Reactive refresh =====

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshScalars();
    private void OnTopicBarsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshBars(_topicBars, _vm.TopicBars, drillable: true);
    private void OnSentimentBarsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshBars(_sentimentBars, _vm.SentimentBars, drillable: false);
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTable();

    private void RefreshScalars()
    {
        _breadcrumb.Text = _vm.Breadcrumb;
        _totalResponses.Text = _vm.TotalResponses.ToString();
        _negative.Text = _vm.NegativeDisplay;
        _avgSentiment.Text = _vm.AverageSentiment;
        _topicTitle.Text = _vm.LevelTitle;

        _drillUp.Visible = _vm.CanDrillUp;
        _topicHint.Visible = _vm.ShowDrillHint;
        _topicPending.Visible = _vm.AnalysisPending;
        _sentimentPending.Visible = _vm.AnalysisPending;
        _emptyHint.Visible = _vm.HasNoResponses;
    }

    // Rebuilds a bar chart from its items: one row each (label | proportional bar | count).
    private void RefreshBars(TableLayoutPanel panel, ObservableCollection<BarItem> bars, bool drillable)
    {
        panel.SuspendLayout();
        foreach (Control old in panel.Controls)
            old.Dispose();
        panel.Controls.Clear();
        panel.RowStyles.Clear();
        panel.RowCount = 0;
        foreach (var bar in bars)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(MakeBarRow(bar, drillable), 0, panel.RowCount);
            panel.RowCount++;
        }
        panel.ResumeLayout();
    }

    // One bar row laid out by a 3-column TableLayoutPanel: label (auto, aligned across rows), the
    // coloured bar (its length is the pre-computed data width), and the count.
    private Control MakeBarRow(BarItem bar, bool drillable)
    {
        var row = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 1, 0, 1) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var name = new Label { Text = bar.Label, AutoSize = true, ForeColor = Theme.BarTrackText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
        var barCell = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
        var fill = new Panel { Height = 16, Width = Math.Max(2, (int)Math.Round(bar.BarWidth)), BackColor = ParseAccent(bar.Accent), Anchor = AnchorStyles.Left };
        barCell.Controls.Add(fill);
        barCell.Resize += (_, _) => fill.Top = Math.Max(0, (barCell.Height - fill.Height) / 2);
        var count = new Label { Text = bar.Count.ToString(), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9f), Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };

        row.Controls.Add(name, 0, 0);
        row.Controls.Add(barCell, 1, 0);
        row.Controls.Add(count, 2, 0);

        if (drillable)
        {
            row.Cursor = Cursors.Hand;
            void Drill(object? s, EventArgs e) => _vm.DrillIntoCommand.Execute(bar.Label);
            row.Click += Drill;
            name.Click += Drill;
            barCell.Click += Drill;
            fill.Click += Drill;
            count.Click += Drill;
        }
        return row;
    }

    private static Color ParseAccent(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return Theme.Accent; }
    }

    private void RefreshTable()
    {
        _grid.Rows.Clear();
        foreach (var row in _vm.Rows)
            _grid.Rows.Add(row.EntryDate, row.Topic, row.Sentiment, row.Excerpt);
    }
}
