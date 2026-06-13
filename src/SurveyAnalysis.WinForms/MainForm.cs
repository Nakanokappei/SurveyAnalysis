using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The application shell — the WinForms counterpart of MainWindow.axaml. It hosts the
// MainWindowViewModel, renders the sidebar from that view model's state, and swaps the content panel
// whenever CurrentPage changes. Sidebar buttons invoke the view model's commands; the view model
// raises events (create / import / edit-schema) that the form turns into modal dialogs.
public sealed class MainForm : Form
{
    private readonly MainWindowViewModel _shell;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.ContentBack };
    private readonly Panel _sidebar = new() { Dock = DockStyle.Left, Width = 300, BackColor = Theme.SidebarBack };

    // The left indent (DIP) where a nav item's text begins = the button's left margin (8) plus its left
    // padding (8). Section labels carry no padding, so they use this same indent to line their text up
    // with the item text. 16 DIP matches the (just-right) top inset, so the content sits a uniform 16
    // DIP from the sidebar edges. Applied via LogicalToDeviceUnits so the alignment holds at any DPI.
    private const int NavTextIndentDip = 16;

    public MainForm()
    {
        // Scale the layout by the monitor DPI so text never clips at >100% Windows scaling.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "アンケート分析";
        // Outer window at near-golden-ratio proportions: 970 × 600 DIP (970/600 ≈ 1.617 ≈ φ). Expressed
        // in DIP via LogicalToDeviceUnits so the window is the intended size at any DPI scaling (raw
        // literals would be device px on this PerMonitorV2 setup — see the sidebar indent note).
        Size = new Size(LogicalToDeviceUnits(970), LogicalToDeviceUnits(600));
        // Don't let the window shrink uselessly small: minimum 800 × 500 DIP (also the outer window).
        MinimumSize = new Size(LogicalToDeviceUnits(800), LogicalToDeviceUnits(500));
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Font();
        BackColor = Theme.ContentBack;

        // Content fills the area; add it first so it sits behind the left-docked sidebar.
        Controls.Add(_content);
        Controls.Add(_sidebar);

        _shell = new MainWindowViewModel(AppServices.Projects, AppServices.Settings, AppServices.Responses, AppServices.Analytics);
        _shell.PropertyChanged += OnShellPropertyChanged;

        // Modal-dialog requests. The design dialog (create / edit / CSV-seeded) is wired here; the
        // import and settings dialogs are migrated in a following step (placeholder notices for now).
        _shell.CreateProjectRequested += OnCreateProject;
        _shell.CreateProjectFromCsvRequested += OnCreateProjectFromCsv;
        _shell.ImportRequested += OnImport;
        _shell.EditSchemaRequested += OnEditSchema;

        RebuildSidebar();
        SwapContent();
    }

    // Re-render the affected region when the shell's state changes: the content on a page swap, the
    // sidebar when a project opens/closes or the 時間別 sub-menu toggles.
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.CurrentPage):
                SwapContent();
                break;
            case nameof(MainWindowViewModel.CurrentProject):
            case nameof(MainWindowViewModel.IsProjectOpen):
            case nameof(MainWindowViewModel.IsTimeExpanded):
                RebuildSidebar();
                break;
        }
    }

    // Replaces the content pane with the view for the current page (ViewFactory resolves the type).
    private void SwapContent()
    {
        var view = ViewFactory.Create(_shell.CurrentPage);
        view.Dock = DockStyle.Fill;
        _content.SuspendLayout();
        foreach (Control old in _content.Controls)
            old.Dispose();
        _content.Controls.Clear();
        _content.Controls.Add(view);
        _content.ResumeLayout();
    }

    // Rebuilds the sidebar from the shell state. Cheap and only fires on open/close/toggle, so a full
    // rebuild keeps the visibility logic in one readable place instead of per-control toggles.
    private void RebuildSidebar()
    {
        _sidebar.SuspendLayout();
        _sidebar.Controls.Clear();

        // With a project open the sidebar carries the long "プロジェクトを閉じる" label, so it keeps the
        // design width (converted for the current DPI, since a Dock=Left width is not auto-scaled on its
        // own). Empty (the welcome screen) it holds almost nothing, so a quarter of the window is plenty.
        // The quarter is taken from the current ClientSize — logical on the first build (then scaled with
        // the form) or device pixels on a later open/close rebuild — so the ratio holds either way.
        _sidebar.Width = _shell.IsProjectOpen
            ? LogicalToDeviceUnits(300)
            : ClientSize.Width / 4;

        // Bottom actions (docked bottom): import / close while a project is open, then 設定 (app-wide).
        var bottom = NavStack();
        bottom.Dock = DockStyle.Bottom;
        bottom.AutoSize = true;
        bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // Bottom inset = 16 DIP to match the top: the 設定 button adds 8 (its bottom padding) + 1 (its
        // bottom margin) below its text, so the panel contributes the remaining 7.
        bottom.Padding = new Padding(0, LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(7));
        if (_shell.IsProjectOpen)
        {
            AddRow(bottom, NavButton("＋ インポート (CSV)", () => _shell.ImportCommand.Execute(null)));
            AddRow(bottom, NavButton("✕ プロジェクトを閉じる", () => _shell.CloseProjectCommand.Execute(null)));
            AddRow(bottom, Divider());
        }
        AddRow(bottom, NavButton("⚙ 設定", OnSettings));

        // Main navigation fills the space above the bottom actions.
        var nav = NavStack();
        nav.Dock = DockStyle.Fill;
        nav.AutoScroll = true;
        nav.Padding = new Padding(0, LogicalToDeviceUnits(14), 0, 0);

        AddRow(nav, new Label
        {
            Text = "プロジェクト",
            ForeColor = Color.White,
            Font = Theme.Font(10f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 4, 16, 2),
        });
        if (_shell.IsProjectOpen && _shell.CurrentProject is { } project)
            AddRow(nav, new Label
            {
                Text = project.Name,
                ForeColor = Theme.ProjectName,
                Font = Theme.Font(9f),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 0, 16, 8),
            });

        if (_shell.IsProjectOpen)
        {
            AddRow(nav, NavButton("▤ ダッシュボード", () => _shell.OpenDashboardCommand.Execute(null)));
            AddRow(nav, NavButton("✎ データ項目", () => _shell.EditSchemaCommand.Execute(null)));
            AddRow(nav, SectionLabel("切り口"));
            AddRow(nav, NavButton(_shell.IsTimeExpanded ? "▾ 時間別" : "▸ 時間別", () => _shell.ToggleTimeCommand.Execute(null)));
            if (_shell.IsTimeExpanded)
            {
                AddRow(nav, SubNavButton("期間", () => _shell.OpenPeriodCommand.Execute(null)));
                AddRow(nav, SubNavButton("曜日", () => _shell.OpenWeekdayCommand.Execute(null)));
            }
            AddRow(nav, NavButton("▸ 地域別", () => _shell.OpenSliceCommand.Execute(SliceKind.Region)));
            AddRow(nav, NavButton("▸ トピック別", () => _shell.OpenSliceCommand.Execute(SliceKind.Topic)));
        }

        // Trailing filler row absorbs the leftover height so the nav buttons stay tight at the top
        // (a Dock=Fill TableLayoutPanel otherwise stretches its last row and the centred button floats).
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nav.Controls.Add(new Panel { BackColor = Theme.SidebarBack, Margin = Padding.Empty }, 0, nav.RowCount);
        nav.RowCount++;

        _sidebar.Controls.Add(nav);     // Fill — add first
        _sidebar.Controls.Add(bottom);  // Bottom
        _sidebar.ResumeLayout();
    }

    // A single-column stack whose children flow downward and stretch to its width (a TableLayoutPanel,
    // so an Anchor=Left|Right child fills the column — FlowLayoutPanel ignores Anchor for sizing).
    private static TableLayoutPanel NavStack()
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            BackColor = Theme.SidebarBack,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    // Appends a control as a new AutoSize row.
    private static void AddRow(TableLayoutPanel stack, Control c)
    {
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(c, 0, stack.RowCount);
        stack.RowCount++;
    }

    // A flat, left-aligned sidebar button that highlights on hover, wired to a command. AutoSize sets
    // its height from the font (DPI-correct, never clips); Anchor=Left|Right stretches it to the column.
    private Button NavButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SidebarBack,
            ForeColor = Theme.NavText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(10f),
            Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(1), LogicalToDeviceUnits(8), LogicalToDeviceUnits(1)),
            Padding = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(8), LogicalToDeviceUnits(6), LogicalToDeviceUnits(8)),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
        button.Click += (_, _) => onClick();
        return button;
    }

    // An indented, smaller sidebar button for the 時間別 → 期間 / 曜日 sub-items.
    private Button SubNavButton(string text, Action onClick)
    {
        var button = NavButton(text, onClick);
        button.ForeColor = Theme.SubNavText;
        button.Font = Theme.Font(9f);
        button.Padding = new Padding(LogicalToDeviceUnits(28), LogicalToDeviceUnits(6), LogicalToDeviceUnits(6), LogicalToDeviceUnits(6));
        return button;
    }

    // A dim section heading ("切り口") above a group of nav buttons; its text lines up with the item text
    // below it via the shared nav indent. Instance method so it can convert that indent for the DPI.
    private Label SectionLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.SectionHeader,
        Font = Theme.Font(8.5f, FontStyle.Bold),
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 12, 0, 4),
    };

    // A thin divider line between the project actions and 設定 (stretches to the column).
    private Panel Divider() => new()
    {
        Height = LogicalToDeviceUnits(1),
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        BackColor = Theme.SidebarHover,
        Margin = new Padding(LogicalToDeviceUnits(14), LogicalToDeviceUnits(8), LogicalToDeviceUnits(14), LogicalToDeviceUnits(8)),
    };

    // 新規プロジェクト作成（モーダル）。確定された下書きを保存して開く。
    private void OnCreateProject()
    {
        using var form = new ProjectDesignForm(new ProjectDesignViewModel());
        if (form.ShowDialog(this) == DialogResult.OK && form.ResultProject is { } project)
            _shell.FinishProjectCreation(project);
    }

    // データ項目の編集（モーダル）。編集モードで開き、保存された下書きを永続化する。
    private void OnEditSchema(Project project)
    {
        using var form = new ProjectDesignForm(new ProjectDesignViewModel(project));
        if (form.ShowDialog(this) == DialogResult.OK && form.ResultProject is { } edited)
            _shell.ApplySchemaEdit(edited);
    }

    // CSV からプロジェクトを作る。ファイルを選び、列から起こしたスキーマを作成ダイアログで確認させ、
    // 確定で保存＋同じCSVの全行を回答として取り込む。
    private void OnCreateProjectFromCsv()
    {
        using var picker = new OpenFileDialog
        {
            Title = "CSV / TSV ファイルからプロジェクトを作成",
            Filter = "CSV / TSV / テキスト (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|すべてのファイル (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        var vm = new ProjectDesignViewModel(File.ReadAllBytes(picker.FileName), Path.GetFileName(picker.FileName));
        if (vm.SourceCsv is null || vm.SourceCsv.Header.Count == 0)
        {
            MessageBox.Show(this, "このファイルから列を読み取れませんでした。", "CSV からプロジェクトを作る",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var form = new ProjectDesignForm(vm);
        if (form.ShowDialog(this) == DialogResult.OK && form.ResultProject is { } project && vm.SourceCsv is { } csv)
            _shell.FinishProjectFromCsv(project, csv, Path.GetFileName(picker.FileName));
    }

    // インポート（モーダル）。CSVを取り込み、回答にマージする。
    private void OnImport(Project project)
    {
        using var form = new ImportForm(new ImportViewModel(project, AppServices.Responses, AppServices.Analytics));
        form.ShowDialog(this);
    }

    // 設定（モーダル）。開くときに保存値を読み込み、閉じたら書き戻す。
    private void OnSettings()
    {
        var viewModel = _shell.CreateSettingsViewModel();
        using var form = new SettingsForm(viewModel);
        form.ShowDialog(this);
        viewModel.Save();
    }

    // Placeholder for a dialog/screen not yet migrated, so navigation never dead-ends during the port.
    private void ShowPending(string what) =>
        MessageBox.Show(this, $"「{what}」は WinForms 版に移植中です。", "移植中",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
}
