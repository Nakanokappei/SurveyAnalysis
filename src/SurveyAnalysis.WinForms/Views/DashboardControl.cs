using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The dashboard — the WinForms counterpart of DashboardView.axaml. Header (title + breadcrumb +
// month picker), an optional drill-up button, three KPI cards, two bar charts (topic / sentiment),
// and the responses table. It binds to the existing DashboardViewModel: scalar labels and visibility
// refresh on its PropertyChanged, the charts and table rebuild when their observable collections
// change (e.g. picking a month or drilling into a topic in the sample project).
internal sealed class DashboardControl : UserControl
{
    private readonly DashboardViewModel _vm;

    // Controls updated reactively.
    private readonly ComboBox _month = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Font = Theme.Font(10f) };
    private readonly Label _breadcrumb = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f) };
    private readonly Button _drillUp = new();
    private readonly Label _totalResponses = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(22f, FontStyle.Bold) };
    private readonly Label _negative = new() { AutoSize = true, ForeColor = Theme.Danger, Font = Theme.Font(22f, FontStyle.Bold) };
    private readonly Label _avgSentiment = new() { AutoSize = true, ForeColor = Theme.Success, Font = Theme.Font(22f, FontStyle.Bold) };
    private readonly Label _topicTitle = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold) };
    private readonly Label _topicHint = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(8.5f) };
    private readonly Label _topicPending = new() { AutoSize = false, ForeColor = Theme.Faint, Font = Theme.Font(9.5f) };
    private readonly FlowLayoutPanel _topicBars = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };
    private readonly Label _sentimentPending = new() { AutoSize = false, ForeColor = Theme.Faint, Font = Theme.Font(9.5f) };
    private readonly FlowLayoutPanel _sentimentBars = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };
    private readonly Label _emptyHint = new() { AutoSize = false, Height = 22, ForeColor = Theme.Faint, Font = Theme.Font(9.5f), Dock = DockStyle.Top };
    private readonly DataGridView _grid = NewGrid();
    private readonly FlowLayoutPanel _flow = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(28),
        BackColor = Theme.ContentBack,
    };

    private bool _syncingMonth;

    public DashboardControl(DashboardViewModel vm)
    {
        _vm = vm;
        BackColor = Theme.ContentBack;
        BuildLayout();

        // Initial population from the view model's current state, then react to later changes.
        RefreshScalars();
        RefreshBars(_topicBars, _vm.TopicBars, drillable: true);
        RefreshBars(_sentimentBars, _vm.SentimentBars, drillable: false);
        RefreshTable();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.TopicBars.CollectionChanged += OnTopicBarsChanged;
        _vm.SentimentBars.CollectionChanged += OnSentimentBarsChanged;
        _vm.Rows.CollectionChanged += OnRowsChanged;
        _month.SelectedIndexChanged += OnMonthSelected;
    }

    // Unsubscribe so a disposed view does not keep its (also-discarded) view model reachable.
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
        // A top-down flow with explicit per-section heights (so a section never collapses), inside a
        // scrolling panel. Section widths follow the panel width via SyncWidths on resize.
        AddSection(BuildHeader(), 64);
        AddSection(BuildDrillUp(), 40);
        AddSection(BuildKpis(), 120);
        AddSection(BuildCharts(), 290);
        AddSection(BuildTable(), 330);
        Controls.Add(_flow);
        _flow.SizeChanged += (_, _) => SyncWidths();
        SyncWidths();
    }

    // Adds a fixed-height section with a little vertical rhythm below it.
    private void AddSection(Control section, int height)
    {
        section.Height = height;
        section.Margin = new Padding(0, 0, 0, 16);
        _flow.Controls.Add(section);
    }

    // Stretches every section to the panel's content width (minus the scrollbar when shown).
    private void SyncWidths()
    {
        var width = _flow.ClientSize.Width - _flow.Padding.Horizontal;
        if (_flow.VerticalScroll.Visible)
            width -= SystemInformation.VerticalScrollBarWidth;
        foreach (Control section in _flow.Controls)
            section.Width = Math.Max(200, width);
    }

    // Header: title + breadcrumb on the left, 対象月 picker on the right.
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 56, BackColor = Theme.ContentBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        titles.Controls.Add(new Label { Text = "ダッシュボード", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(_breadcrumb);

        var picker = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right };
        picker.Controls.Add(new Label { Text = "対象月", AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(10f), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 8, 0) });
        picker.Controls.Add(_month);

        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(picker, 1, 0);
        return header;
    }

    private Control BuildDrillUp()
    {
        _drillUp.Text = "↩ 集計に戻る";
        _drillUp.AutoSize = true;
        _drillUp.FlatStyle = FlatStyle.Flat;
        _drillUp.BackColor = Theme.CardBorder;
        _drillUp.ForeColor = Theme.TitleText;
        _drillUp.Font = Theme.Font(9.5f);
        _drillUp.Padding = new Padding(14, 7, 14, 7);
        _drillUp.FlatAppearance.BorderSize = 0;
        _drillUp.Cursor = Cursors.Hand;
        _drillUp.Click += (_, _) => _vm.DrillUpCommand.Execute(null);
        // Host left-aligned in a thin row.
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.ContentBack };
        row.Controls.Add(_drillUp);
        return row;
    }

    // Three equal KPI cards.
    private Control BuildKpis()
    {
        var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 104, BackColor = Theme.ContentBack };
        for (var i = 0; i < 3; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        grid.Controls.Add(KpiCard("総回答数", _totalResponses, "件"), 0, 0);
        grid.Controls.Add(KpiCard("ネガティブ件数", _negative, "要対応 件数"), 1, 0);
        grid.Controls.Add(KpiCard("平均感情スコア", _avgSentiment, "-1.0 〜 +1.0"), 2, 0);
        return grid;
    }

    private static Control KpiCard(string title, Label value, string subtitle)
    {
        // Explicit positions inside the card (the title at top, the large value, then the subtitle):
        // deterministic vertical placement that always fits the card height.
        var card = Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 16, 0);
        value.AutoSize = true;
        value.Location = new Point(0, 22);
        card.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(10f), Location = new Point(0, 0) });
        card.Controls.Add(value);
        card.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Font(8.5f), Location = new Point(0, 60) });
        return card;
    }

    // Two equal chart cards: topic (clickable bars) and sentiment.
    private Control BuildCharts()
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 280, BackColor = Theme.ContentBack };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var topicCard = Card();
        topicCard.Dock = DockStyle.Fill;
        topicCard.Margin = new Padding(0, 0, 9, 0);
        var topicStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.White };
        topicStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topicStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topicStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topicStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _topicTitle.Margin = new Padding(0, 0, 0, 6);
        _topicHint.Margin = new Padding(0, 0, 0, 4);
        _topicPending.Dock = DockStyle.Top;
        _topicPending.Height = 40;
        _topicPending.Text = "トピック分析は LLM 連携後に表示されます。";
        topicStack.Controls.Add(_topicTitle, 0, 0);
        topicStack.Controls.Add(_topicHint, 0, 1);
        topicStack.Controls.Add(_topicPending, 0, 2);
        topicStack.Controls.Add(_topicBars, 0, 3);
        topicCard.Controls.Add(topicStack);

        var sentimentCard = Card();
        sentimentCard.Dock = DockStyle.Fill;
        sentimentCard.Margin = new Padding(9, 0, 0, 0);
        var sentimentStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.White };
        sentimentStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sentimentStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sentimentStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sentimentPending.Dock = DockStyle.Top;
        _sentimentPending.Height = 40;
        _sentimentPending.Text = "感情分析は LLM 連携後に表示されます。";
        sentimentStack.Controls.Add(new Label { Text = "感情極性の分布", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        sentimentStack.Controls.Add(_sentimentPending, 0, 1);
        sentimentStack.Controls.Add(_sentimentBars, 0, 2);
        sentimentCard.Controls.Add(sentimentStack);

        grid.Controls.Add(topicCard, 0, 0);
        grid.Controls.Add(sentimentCard, 1, 0);
        return grid;
    }

    private Control BuildTable()
    {
        var card = Card();
        card.Height = 320;
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.White };
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.Controls.Add(new Label { Text = "回答一覧（抜粋）", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        _emptyHint.Text = "まだ回答がありません。サイドバーの「インポート (CSV)」から取り込めます。";
        stack.Controls.Add(_emptyHint, 0, 1);
        _grid.Dock = DockStyle.Fill;
        stack.Controls.Add(_grid, 0, 2);
        card.Controls.Add(stack);
        return card;
    }

    private static Panel Card() => new()
    {
        BackColor = Color.White,
        Padding = new Padding(18),
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static DataGridView NewGrid()
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            Font = Theme.Font(9.5f),
            EnableHeadersVisualStyles = false,
        };
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Font(9.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "記入日", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "トピック", Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "感情", Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "抜粋（フリーテキスト）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        return grid;
    }

    // ===== Reactive refresh =====

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshScalars();
    private void OnTopicBarsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshBars(_topicBars, _vm.TopicBars, drillable: true);
    private void OnSentimentBarsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshBars(_sentimentBars, _vm.SentimentBars, drillable: false);
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTable();

    private void OnMonthSelected(object? sender, EventArgs e)
    {
        if (!_syncingMonth && _month.SelectedItem is string month)
            _vm.Month = month;
    }

    // Pushes every scalar / visibility value from the view model onto the controls.
    private void RefreshScalars()
    {
        _syncingMonth = true;
        if (_month.DataSource is null)
            _month.DataSource = _vm.Months;
        _month.SelectedItem = _vm.Month;
        _syncingMonth = false;

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

    // Rebuilds a bar chart panel from its bar items, scaling each bar to its pre-computed width.
    private void RefreshBars(FlowLayoutPanel panel, System.Collections.ObjectModel.ObservableCollection<BarItem> bars, bool drillable)
    {
        panel.SuspendLayout();
        foreach (Control old in panel.Controls)
            old.Dispose();
        panel.Controls.Clear();
        foreach (var bar in bars)
            panel.Controls.Add(MakeBarRow(bar, drillable));
        panel.ResumeLayout();
    }

    // One bar row: label (fixed column) + a coloured bar of the pre-scaled width + the count.
    private Control MakeBarRow(BarItem bar, bool drillable)
    {
        var row = new Panel { Width = 360, Height = 28, BackColor = Color.White, Margin = new Padding(0, 2, 0, 2) };
        var name = new Label { Text = bar.Label, AutoSize = false, Location = new Point(0, 4), Size = new Size(130, 20), ForeColor = Theme.BarTrackText, Font = Theme.Font(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var width = Math.Max(2, (int)Math.Round(bar.BarWidth));
        var fill = new Panel { Location = new Point(136, 5), Size = new Size(width, 18), BackColor = ParseAccent(bar.Accent) };
        var count = new Label { Text = bar.Count.ToString(), AutoSize = true, Location = new Point(136 + width + 8, 6), ForeColor = Theme.Muted, Font = Theme.Font(9f) };
        row.Controls.Add(name);
        row.Controls.Add(fill);
        row.Controls.Add(count);
        if (drillable)
        {
            row.Cursor = Cursors.Hand;
            void Drill(object? s, EventArgs e) => _vm.DrillIntoCommand.Execute(bar.Label);
            row.Click += Drill;
            name.Click += Drill;
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

    // Repopulates the responses table from the view model rows.
    private void RefreshTable()
    {
        _grid.Rows.Clear();
        foreach (var row in _vm.Rows)
            _grid.Rows.Add(row.EntryDate, row.Topic, row.Sentiment, row.Excerpt);
    }
}
