using System;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The welcome screen shown when no project is open — the WinForms counterpart of WelcomeView.axaml.
// A centered card shows the saved projects as a file-style list (a DataGridView with an explicit 開く
// link per row) and offers the three entry points. Built from layout containers (a TableLayoutPanel
// centers the card, the card stacks its rows); the only sizes are the card width (1.5× the heading's
// measured width) and the list height (eight rows), and inter-control spacing is expressed in DIP via
// LogicalToDeviceUnits — this control is not auto-scaled, so raw literals would be device pixels (see
// the sidebar indent note in MainForm).
internal sealed class WelcomeControl : UserControl
{
    // The heading text; its natural one-line width drives the whole card's width (below).
    private const string HeadingText = "プロジェクトを開始しましょう";

    // The card's content width = the heading's natural one-line width, so the heading never wraps and
    // the body text / list / buttons line up under it. Measured with the same font GDI+ uses to draw
    // the heading, so the value equals the rendered heading width at whatever DPI is in effect.
    internal static readonly int ContentWidth = MeasureHeadingWidth();

    private static int MeasureHeadingWidth()
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = Theme.Font(20f, FontStyle.Bold);
        // Round up with a little slack so the heading always stays on one line.
        return (int)Math.Ceiling(graphics.MeasureString(HeadingText, font).Width) + 8;
    }

    // The card (and the file list / buttons in it) is 1.5× the heading width, so the list has room to
    // read like a real file pane. The heading and lead text are centered within this wider card.
    private static int CardWidth => ContentWidth * 3 / 2;

    private readonly WelcomeViewModel _vm;

    public WelcomeControl(WelcomeViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;

        // Single cell that centers the card both ways.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = Theme.ContentBack };

        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Anchor = AnchorStyles.None,
            BackColor = Theme.ContentBack,
        };
        // A fixed-width column = the card width (1.5× the heading). The list and buttons fill it; the
        // heading and lead text are centered within it (Anchor=None).
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CardWidth));

        card.Controls.Add(Title(HeadingText));
        card.Controls.Add(Body("プロジェクトを作成すると、スキャン画像の読み込み・トピック割り当て・感情分析・月次レポートを管理できます。"));

        // Saved projects as a file-style list (only when there are any).
        if (_vm.HasRecentProjects)
            card.Controls.Add(RecentProjectsList());

        // The three actions live in a fixed-width (ContentWidth) column that is centered in the wider
        // card (Anchor=None). Every button fills that column (Anchor=Left|Right), so they are all the
        // same width and the group is centred — regardless of each button's own content width.
        var buttons = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Anchor = AnchorStyles.None,
            BackColor = Theme.ContentBack,
            Margin = new Padding(0),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));
        buttons.Controls.Add(PrimaryButton("➕", "プロジェクトを作る", () => _vm.CreateProjectCommand.Execute(null)));
        buttons.Controls.Add(OutlineButton("CSV からプロジェクトを作る", () => _vm.CreateFromCsvCommand.Execute(null)));
        buttons.Controls.Add(LinkButton("サンプルプロジェクトを開く", () => _vm.OpenSampleCommand.Execute(null)));
        card.Controls.Add(buttons);

        root.Controls.Add(card, 0, 0);
        Controls.Add(root);
    }

    private static Label Title(string text) => new()
    {
        Text = text,
        Font = Theme.Font(20f, FontStyle.Bold),
        ForeColor = Theme.TitleText,
        AutoSize = true,
        // GDI+ rendering so the on-screen width matches the GDI+ measurement that sized the card.
        UseCompatibleTextRendering = true,
        // Left-aligned to the card's left edge (the file list's left edge).
        Anchor = AnchorStyles.Top | AnchorStyles.Left,
        MaximumSize = new Size(ContentWidth, 0),
        Margin = new Padding(0, 0, 0, 8),
    };

    private static Label Body(string text) => new()
    {
        Text = text,
        Font = Theme.Font(9.5f),
        ForeColor = Theme.BodyText,
        AutoSize = true,
        // Matches the heading width and is left-aligned under it (to the file list's left edge).
        Anchor = AnchorStyles.Top | AnchorStyles.Left,
        MaximumSize = new Size(ContentWidth, 0),
        Margin = new Padding(0, 0, 0, 16),
    };

    // The saved projects as a file-style list (a DataGridView, like the dashboard's tables): a header
    // row, one row per project, and an explicit "開く" link in every row so the open action is visible
    // rather than relying on double-click. PII-free — only the name, field count and last-updated time
    // (we record updates, not opens). Double-clicking a row opens it too.
    private DataGridView RecentProjectsList()
    {
        var rowFont = Theme.Font(9.5f);
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            // No gridlines between cells — a flat list, not a spreadsheet.
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            Font = rowFont,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 0),
        };
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Font(9.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        // Keep a soft row-selection highlight (not the heavy system blue) so the chosen row is clear.
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = Theme.TitleText;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xD6, 0xE6, 0xF5);
        grid.DefaultCellStyle.SelectionForeColor = Theme.TitleText;
        // Don't let the clicked cell's column header look "selected": render it like a normal header.
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;

        // Columns fill the card width by weight: name, field count, last-updated, then the 開く link.
        var nameCol = new DataGridViewTextBoxColumn { HeaderText = "最近のプロジェクト", FillWeight = 44, SortMode = DataGridViewColumnSortMode.NotSortable };
        var countCol = new DataGridViewTextBoxColumn { HeaderText = "項目数", FillWeight = 16, SortMode = DataGridViewColumnSortMode.NotSortable };
        countCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        var dateCol = new DataGridViewTextBoxColumn { HeaderText = "最終更新日時", FillWeight = 26, SortMode = DataGridViewColumnSortMode.NotSortable };
        var openCol = new DataGridViewLinkColumn { HeaderText = "", Text = "開く", UseColumnTextForLinkValue = true, FillWeight = 14, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, TrackVisitedState = false, SortMode = DataGridViewColumnSortMode.NotSortable };
        openCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns.AddRange(nameCol, countCol, dateCol, openCol);

        // Fixed row/header height (DPI-scaled) so the list reliably shows eight rows.
        var rowHeight = rowFont.Height + LogicalToDeviceUnits(6);
        grid.RowTemplate.Height = rowHeight;
        grid.ColumnHeadersHeight = rowHeight;

        foreach (var summary in _vm.RecentProjects)
        {
            // Project names are unique, so the name alone identifies a project — no path is shown.
            var i = grid.Rows.Add(summary.Name, summary.FieldCount.ToString(), summary.UpdatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), null);
            grid.Rows[i].Tag = summary;
        }

        // Sized for eight rows plus the header; beyond eight projects the grid scrolls. MinimumSize (not
        // just Height) is required because an AutoSize TableLayoutPanel row otherwise shrinks the grid
        // to its ~1-row preferred height, clipping it.
        var height = rowHeight * 9;
        grid.Height = height;
        grid.MinimumSize = new Size(0, height);

        // Opening swaps the content pane and disposes this control (and the grid), but the grid is still
        // inside its click WndProc — BeginInvoke runs the open after that returns, so the grid isn't torn
        // down mid-message.
        void Open(int rowIndex)
        {
            if (rowIndex >= 0 && grid.Rows[rowIndex].Tag is ProjectSummary summary)
                BeginInvoke(() => _vm.OpenProjectCommand.Execute(summary));
        }
        grid.CellContentClick += (_, e) => { if (e.ColumnIndex == openCol.Index) Open(e.RowIndex); };
        grid.CellDoubleClick += (_, e) => Open(e.RowIndex);
        return grid;
    }

    // An action button that fills the centred ContentWidth buttons column (Anchor=Left|Right), so all
    // three are the same width; AutoSize + Padding set the height. An 8 DIP top margin gives a uniform
    // gap between the stacked buttons. Instance method so the gap can be converted for the DPI.
    private Button BaseButton(string glyph, string text, Action onClick) =>
        WithClick(new IconButton
        {
            Glyph = glyph,
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Font = Theme.Font(10.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12, 9, 12, 9),
            Margin = new Padding(0, LogicalToDeviceUnits(8), 0, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        }, onClick);

    private Button PrimaryButton(string glyph, string text, Action onClick)
    {
        var button = BaseButton(glyph, text, onClick);
        button.BackColor = Theme.Accent;
        button.ForeColor = Theme.AccentText;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private Button OutlineButton(string text, Action onClick)
    {
        var button = BaseButton("", text, onClick);
        button.BackColor = Color.White;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private Button LinkButton(string text, Action onClick)
    {
        var button = BaseButton("", text, onClick);
        button.BackColor = Theme.ContentBack;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 0;
        button.Font = Theme.Font(9.5f);
        return button;
    }

    private static Button WithClick(Button button, Action onClick)
    {
        button.Click += (_, _) => onClick();
        return button;
    }
}
