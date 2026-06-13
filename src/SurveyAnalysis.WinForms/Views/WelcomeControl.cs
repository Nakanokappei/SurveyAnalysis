using System;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The welcome screen shown when no project is open — the WinForms counterpart of WelcomeView.axaml.
// A centered card shows the saved projects as a file-style list (ListView in Details view, like
// Explorer's detail pane) and offers the three entry points. Built from layout containers (a
// TableLayoutPanel centers the card, the card stacks its rows); the only sizes are the card width
// (the heading's measured width) and the list height (its row count), and inter-control spacing is
// expressed in DIP via LogicalToDeviceUnits — this control is not auto-scaled, so raw literals would
// be device pixels (see the sidebar indent note in MainForm).
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

        card.Controls.Add(PrimaryButton("＋ プロジェクトを作る", () => _vm.CreateProjectCommand.Execute(null)));
        card.Controls.Add(OutlineButton("CSV からプロジェクトを作る", () => _vm.CreateFromCsvCommand.Execute(null)));
        card.Controls.Add(LinkButton("サンプルプロジェクトを開く", () => _vm.OpenSampleCommand.Execute(null)));

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

    // The saved projects as a file-style list: a header row of columns, one row per project, reopened
    // on activate (double-click / Enter) — reading like Explorer's detail pane. PII-free: only the
    // project name, its field count and its last-updated time (we record updates, not opens).
    private ListView RecentProjectsList()
    {
        var rowFont = Theme.Font(9.5f);
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            Font = rowFont,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 0),
        };

        // Column widths apportioned from the card width (device pixels, like the heading measurement).
        list.Columns.Add("最近のプロジェクト", CardWidth / 2);
        list.Columns.Add("項目数", CardWidth * 16 / 100, HorizontalAlignment.Right);
        list.Columns.Add("最終更新日時", CardWidth - CardWidth / 2 - CardWidth * 16 / 100);

        foreach (var summary in _vm.RecentProjects)
        {
            var item = new ListViewItem(summary.Name) { Tag = summary };
            item.SubItems.Add(summary.FieldCount.ToString());
            item.SubItems.Add(summary.UpdatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"));
            list.Items.Add(item);
        }

        // Sized to show ten rows plus the header; beyond ten projects the list scrolls. MinimumSize
        // (not just Height) is required because an AutoSize TableLayoutPanel row otherwise shrinks the
        // list to its ~1-row preferred height, clipping it.
        var rowHeight = rowFont.Height + LogicalToDeviceUnits(2);
        var height = rowHeight * 11; // header + 10 rows
        list.Height = height;
        list.MinimumSize = new Size(0, height);

        list.ItemActivate += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ProjectSummary summary)
                _vm.OpenProjectCommand.Execute(summary);
        };
        return list;
    }

    // Action button at the heading width (ContentWidth), centered under the heading rather than spanning
    // the wider card. AutoSize + Padding set its height; MinimumSize pins its width. An 8 DIP top margin
    // gives a uniform 8 DIP gap between the stacked buttons (the control above carries no bottom margin).
    // Instance method so the width/gap can be converted for the DPI.
    private Button BaseButton(string text, Action onClick) =>
        WithClick(new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            MinimumSize = new Size(ContentWidth, 0),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Font(10.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12, 9, 12, 9),
            Margin = new Padding(0, LogicalToDeviceUnits(8), 0, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        }, onClick);

    private Button PrimaryButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
        button.BackColor = Theme.Accent;
        button.ForeColor = Theme.AccentText;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private Button OutlineButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
        button.BackColor = Color.White;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private Button LinkButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
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
