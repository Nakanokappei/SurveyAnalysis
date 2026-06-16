using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
        // Reopen where it was last closed (position / size / maximized) when the screen is unchanged;
        // otherwise the defaults above (centered, 970 × 600) stand.
        RestoreWindowState();
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

    // ===== Database maintenance (auto-optimize on startup / backup on close) =====

    // On startup, optimize (VACUUM) the database if auto-optimize is on (the default). Quick on a small
    // file; the busy cursor marks the brief pause. Best-effort — a failure must not block launch.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_shell.CreateSettingsViewModel().AutoOptimizeOnStartup)
            return;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            UseWaitCursor = true;
            AppServices.Maintenance.Optimize();
        }
        catch { /* optimization is best-effort */ }
        finally { UseWaitCursor = false; Cursor.Current = Cursors.Default; }
    }

    // プロジェクトを閉じる: back up the database first (the requested trigger), then return to the welcome page.
    private void OnCloseProject()
    {
        BackupDatabase(announceErrors: true);
        _shell.CloseProjectCommand.Execute(null);
    }

    // Copies the database to the backups folder per the retention setting, with the busy cursor shown.
    // A backup failure on an explicit close is surfaced; on app exit it is swallowed (never block exit).
    private void BackupDatabase(bool announceErrors)
    {
        var retention = BackupRetentionPolicy.Parse(_shell.CreateSettingsViewModel().BackupRetention);
        if (retention == BackupRetention.Off)
            return;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            UseWaitCursor = true;
            AppServices.Maintenance.Backup(retention, DateTime.Now);
        }
        catch (Exception ex) when (announceErrors)
        {
            MessageBox.Show(this, "データベースのバックアップに失敗しました。\n" + ex.Message, "バックアップ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { /* on exit: never block shutdown */ }
        finally { UseWaitCursor = false; Cursor.Current = Cursors.Default; }
    }

    // ===== Window position / size persistence =====
    // Remember the window's normal bounds, maximized state and the screen size at exit, and restore them
    // next launch — but only when the screen is the same size, so a layout that fit one monitor is not
    // forced onto a different one. Stored in the shared key/value settings store under window.* keys.

    private const string WindowBoundsKey = "window.bounds";
    private const string WindowMaximizedKey = "window.maximized";
    private const string WindowScreenKey = "window.screen";

    private void RestoreWindowState()
    {
        if (Screen.PrimaryScreen is not { } screen)
            return;
        var settings = AppServices.Settings.LoadAll();
        // Restore only when the screen is the same size as when the bounds were saved.
        if (!settings.TryGetValue(WindowScreenKey, out var savedScreen) || savedScreen != ScreenSizeKey(screen))
            return;
        if (!settings.TryGetValue(WindowBoundsKey, out var savedBounds) || !TryParseBounds(savedBounds, out var bounds))
            return;

        StartPosition = FormStartPosition.Manual;

        // If other instances of this app are already open, cascade this window off the saved position so
        // it does not land exactly on top of them (instead of re-displaying at the same spot). A duplicate
        // also skips restoring a maximized state — a cascaded, normal window reads clearly as "another".
        var duplicates = CountOtherInstances();
        if (duplicates > 0)
        {
            Bounds = CascadeBounds(bounds, duplicates, screen);
            return;
        }

        Bounds = bounds;
        if (settings.TryGetValue(WindowMaximizedKey, out var maxText) && bool.TryParse(maxText, out var maximized) && maximized)
            WindowState = FormWindowState.Maximized;
    }

    // The number of other running processes of this app (same executable name).
    private static int CountOtherInstances()
    {
        using var current = Process.GetCurrentProcess();
        var count = 0;
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id != current.Id)
                count++;
            process.Dispose();
        }
        return count;
    }

    // Offsets the saved bounds down-right by one step per existing instance so stacked windows cascade
    // instead of overlapping; wraps back toward the top-left if a step would push the window off-screen.
    private Rectangle CascadeBounds(Rectangle bounds, int instances, Screen screen)
    {
        var area = screen.WorkingArea;
        var step = LogicalToDeviceUnits(28) * instances;
        var x = bounds.X + step;
        var y = bounds.Y + step;
        if (x + bounds.Width > area.Right)
            x = area.X + LogicalToDeviceUnits(28);
        if (y + bounds.Height > area.Bottom)
            y = area.Y + LogicalToDeviceUnits(28);
        return new Rectangle(x, y, bounds.Width, bounds.Height);
    }

    private void SaveWindowState()
    {
        if (Screen.PrimaryScreen is not { } screen)
            return;
        // Save the restore (normal) bounds so a maximized/minimized window still remembers its real size.
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        AppServices.Settings.Save(new Dictionary<string, string>
        {
            [WindowBoundsKey] = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}",
            [WindowMaximizedKey] = (WindowState == FormWindowState.Maximized).ToString(),
            [WindowScreenKey] = ScreenSizeKey(screen),
        });
    }

    private static string ScreenSizeKey(Screen screen) => $"{screen.Bounds.Width},{screen.Bounds.Height}";

    private static bool TryParseBounds(string text, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var parts = text.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
            return false;
        bounds = new Rectangle(x, y, w, h);
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Capture the working database on exit too (the requested trigger is closing a project, but quitting
        // with a project still open should not skip the safety backup). Silent so it never blocks shutdown.
        if (_shell.IsProjectOpen)
            BackupDatabase(announceErrors: false);
        SaveWindowState();
        base.OnFormClosing(e);
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

        // Bottom actions (docked bottom). While a project is open: the data I/O pair (import / export),
        // then the schema editor (データ項目), then close — each group set off by a divider. 設定 is
        // app-wide and always present at the very bottom.
        var bottom = NavStack();
        bottom.Dock = DockStyle.Bottom;
        bottom.AutoSize = true;
        bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // Bottom inset = 16 DIP to match the top: the 設定 button adds 2 (its bottom padding) below its
        // text (no bottom margin), so the panel contributes the remaining 14.
        bottom.Padding = new Padding(0, LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(14));
        if (_shell.IsProjectOpen)
        {
            AddRow(bottom, NavButton(Icons.Add, "インポート (CSV)", () => _shell.ImportCommand.Execute(null)));
            AddRow(bottom, NavButton(Icons.Export, "エクスポート", OnExportNotImplemented));
            AddRow(bottom, Divider());
            AddRow(bottom, NavButton(Icons.Close, "プロジェクトを閉じる", OnCloseProject));
        }
        AddRow(bottom, NavButton(Icons.Settings, "設定", OnSettings));

        // Main navigation fills the space above the bottom actions. No AutoScroll: the few nav rows always
        // fit, and the trailing filler row absorbs the slack — AutoScroll only produced a phantom
        // scrollbar (a TableLayoutPanel with a Percent filler row reports content just over its height).
        var nav = NavStack();
        nav.Dock = DockStyle.Fill;
        nav.Padding = new Padding(0, LogicalToDeviceUnits(14), 0, 0);

        var heading = new Label
        {
            Text = "プロジェクト",
            ForeColor = Color.White,
            Font = Theme.Font(10f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 4, 8, 2),
        };
        if (_shell.IsProjectOpen && _shell.CurrentProject is { } project)
        {
            // Heading + project name on the left, the 2-line-tall 構成 (project configuration) button on
            // the right — it opens the same schema dialog the old bottom データ項目 entry did.
            var nameLabel = new Label
            {
                Text = project.Name,
                ForeColor = Theme.ProjectName,
                Font = Theme.Font(9f),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 0, 8, 8),
            };
            var textStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, BackColor = Theme.SidebarBack, Margin = Padding.Empty };
            textStack.Controls.Add(heading);
            textStack.Controls.Add(nameLabel);

            var block = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.SidebarBack, Margin = Padding.Empty };
            block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            block.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            block.Controls.Add(textStack, 0, 0);
            block.Controls.Add(ConfigureButton(), 1, 0);
            AddRow(nav, block);
        }
        else
        {
            AddRow(nav, heading);
        }

        if (_shell.IsProjectOpen)
        {
            AddRow(nav, NavButton(Icons.Dashboard, "ダッシュボード", () => _shell.OpenDashboardCommand.Execute(null)));
            AddRow(nav, SectionLabel("切り口"));
            AddRow(nav, NavButton(_shell.IsTimeExpanded ? Icons.Expand : Icons.Collapse, "時間別", () => _shell.ToggleTimeCommand.Execute(null)));
            if (_shell.IsTimeExpanded)
            {
                AddRow(nav, SubNavButton("期間", () => _shell.OpenPeriodCommand.Execute(null)));
                AddRow(nav, SubNavButton("曜日", () => _shell.OpenWeekdayCommand.Execute(null)));
            }
            AddRow(nav, NavButton(Icons.Bullet, "地域別", () => _shell.OpenSliceCommand.Execute(SliceKind.Region)));
            AddRow(nav, NavButton(Icons.Bullet, "トピック別", () => _shell.OpenSliceCommand.Execute(SliceKind.Topic)));
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

    // A flat, left-aligned sidebar button that highlights on hover, wired to a command. The leading icon
    // glyph is drawn in Segoe UI Emoji (via IconButton) so it never breaks; pass "" for no icon.
    // Anchor=Left|Right stretches it to the column; the height is fixed so the 8 DIP gaps stay tight.
    private IconButton NavButton((string Font, string Glyph) icon, string text, Action onClick)
    {
        var button = new IconButton
        {
            Glyph = icon.Glyph,
            IconFontName = icon.Font,
            Text = text,
            // Fixed compact height (AutoSize made the buttons ~27 DIP tall — far more than the text —
            // which dwarfed the gaps; a fixed height keeps the rows tight and the 8 DIP gap visible).
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Theme.SidebarBack,
            ForeColor = Theme.NavText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(10f),
            // 8 DIP gap between items, made by the top margin only (the item above has no bottom margin),
            // so the spacing is exactly 8 DIP rather than the sum of two margins.
            Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(8), LogicalToDeviceUnits(8), 0),
            // Only the left indent matters now (height is fixed); no vertical padding.
            Padding = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(6), 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.Height = LogicalToDeviceUnits(20);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
        button.Click += (_, _) => onClick();
        return button;
    }

    // An indented, smaller sidebar button (no icon) for the 時間別 → 期間 / 曜日 sub-items.
    private IconButton SubNavButton(string text, Action onClick)
    {
        var button = NavButton(Icons.None, text, onClick);
        button.ForeColor = Theme.SubNavText;
        button.Font = Theme.Font(9f);
        // Sub-items hug their parent (時間別) and each other — a 2 DIP gap vs the 8 DIP between top-level
        // items — so the menu / sub-menu hierarchy reads. Slightly shorter than a top-level item.
        button.Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(2), LogicalToDeviceUnits(8), 0);
        button.Padding = new Padding(LogicalToDeviceUnits(28), 0, LogicalToDeviceUnits(6), 0);
        button.Height = LogicalToDeviceUnits(18);
        return button;
    }

    // The 構成 (project configuration) button in the sidebar header — a bordered, 2-line-tall pill that
    // opens the schema dialog (formerly the bottom データ項目 entry). The vertical padding makes it about
    // as tall as the heading + project-name block beside it; it is centred vertically in that block.
    private IconButton ConfigureButton()
    {
        var button = new IconButton
        {
            Glyph = Icons.Edit.Glyph,
            IconFontName = Icons.Edit.Font,
            Text = "構成",
            IconSize = 9.5f,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            BackColor = Theme.SidebarBack,
            ForeColor = Theme.NavText,
            Font = Theme.Font(9.5f),
            Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(10), LogicalToDeviceUnits(12), LogicalToDeviceUnits(10)),
            Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Theme.SidebarHover;
        button.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
        button.Click += (_, _) => _shell.EditSchemaCommand.Execute(null);
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
        Margin = new Padding(LogicalToDeviceUnits(14), LogicalToDeviceUnits(8), LogicalToDeviceUnits(14), 0),
    };

    // 新規プロジェクト作成（モーダル）。確定された下書きを保存して開く。
    private void OnCreateProject()
    {
        var vm = new ProjectDesignViewModel { IsNameAvailable = name => _shell.IsProjectNameAvailable(name, 0) };
        using var form = new ProjectDesignForm(vm);
        if (form.ShowDialog(this) == DialogResult.OK && form.ResultProject is { } project)
            _shell.FinishProjectCreation(project);
    }

    // データ項目の編集（モーダル）。編集モードで開き、保存された下書きを永続化する。
    private void OnEditSchema(Project project)
    {
        // Loading the schema and building the table takes a noticeable beat; show a busy cursor so the
        // click is acknowledged until the dialog appears. The construction runs synchronously (no message
        // pump), so the wait cursor set here stays up until ShowDialog brings the dialog forward.
        Cursor.Current = Cursors.WaitCursor;
        var vm = new ProjectDesignViewModel(project) { IsNameAvailable = name => _shell.IsProjectNameAvailable(name, project.Id) };
        using var form = new ProjectDesignForm(vm);
        var result = form.ShowDialog(this);
        if (form.DeleteConfirmed)
            _shell.DeleteCurrentProject();
        else if (result == DialogResult.OK && form.ResultProject is { } edited)
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

        // Reading + parsing the CSV and building the design dialog takes a noticeable beat; show a busy
        // cursor so the file pick is acknowledged until the dialog appears (same as OnEditSchema).
        Cursor.Current = Cursors.WaitCursor;
        var vm = new ProjectDesignViewModel(File.ReadAllBytes(picker.FileName), Path.GetFileName(picker.FileName));
        if (vm.SourceCsv is null || vm.SourceCsv.Header.Count == 0)
        {
            MessageBox.Show(this, "このファイルから列を読み取れませんでした。", "CSV からプロジェクトを作る",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        vm.IsNameAvailable = name => _shell.IsProjectNameAvailable(name, 0);

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

    // エクスポートはメニューだけ先に用意した段階（機能は未実装）。
    private void OnExportNotImplemented() =>
        MessageBox.Show(this, "エクスポート機能は今後実装予定です。", "エクスポート",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    // Placeholder for a dialog/screen not yet migrated, so navigation never dead-ends during the port.
    private void ShowPending(string what) =>
        MessageBox.Show(this, $"「{what}」は WinForms 版に移植中です。", "移植中",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
}
