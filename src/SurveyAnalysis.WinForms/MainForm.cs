using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SurveyAnalysis.Data;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using SurveyAnalysis.Reports;
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
        _shell.ImportImagesRequested += OnImportImages;
        _shell.ImportImagesFromFolderRequested += OnImportImagesFromFolder;
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
            case nameof(MainWindowViewModel.IsPeriodExpanded):
            case nameof(MainWindowViewModel.IsWeekdayExpanded):
            case nameof(MainWindowViewModel.IsRegionExpanded):
            case nameof(MainWindowViewModel.IsTopicExpanded):
            case nameof(MainWindowViewModel.IsChoiceExpanded):
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
            AddRow(bottom, NavButton(Icons.Image, "画像を読み込む", () => _shell.ImportImagesCommand.Execute(null)));
            AddRow(bottom, NavButton(Icons.Folder, "フォルダから画像を読み込む", () => _shell.ImportImagesFromFolderCommand.Execute(null)));
            AddRow(bottom, NavButton(Icons.Export, "エクスポート", OnExport));
            // 閉じる is a project action too, so no divider line above it — but it is set apart from the
            // import/export pair by extra spacing (a wider top margin) so it still reads as distinct.
            var close = NavButton(Icons.Close, "プロジェクトを閉じる", OnCloseProject);
            close.Margin = new Padding(close.Margin.Left, LogicalToDeviceUnits(20), close.Margin.Right, close.Margin.Bottom);
            AddRow(bottom, close);
            AddRow(bottom, Divider());   // set 設定 off from the project actions above it
        }
        AddRow(bottom, NavButton(Icons.Settings, "設定", OnSettings));

        // Main navigation fills the space above the bottom actions. It can now grow tall (each 軸 expands
        // into a 質問 sub-menu), so it sits in an AutoScroll host and the stack itself is Top + AutoSize —
        // it scrolls when the expanded menus exceed the height instead of clipping.
        var navScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.SidebarBack };
        var nav = NavStack();
        nav.Dock = DockStyle.Top;
        nav.AutoSize = true;
        nav.AutoSizeMode = AutoSizeMode.GrowAndShrink;
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

            // 期間別 / 曜日別 / 地域別: clicking the axis opens its summary report and toggles a 質問 sub-menu;
            // each 質問 sub-item opens that axis × question cross-tab.
            AddAxis(nav, "期間別", _shell.IsPeriodExpanded, () => _shell.OpenPeriodCommand.Execute(null), AnalysisGrouping.Time);
            AddAxis(nav, "曜日別", _shell.IsWeekdayExpanded, () => _shell.OpenWeekdayCommand.Execute(null), AnalysisGrouping.Weekday);
            AddAxis(nav, "地域別", _shell.IsRegionExpanded, () => _shell.OpenRegionCommand.Execute(null), AnalysisGrouping.Region);

            // トピック別 / 選択肢別: expandable only — one sub-item per 自由記述 / 選択肢 question, each opening
            // that question's own report (topics / options as the rows).
            AddRow(nav, NavButton(_shell.IsTopicExpanded ? Icons.Expand : Icons.Collapse, "トピック別", () => _shell.ToggleTopicCommand.Execute(null)));
            if (_shell.IsTopicExpanded)
                AddQuestionSubmenu(nav, _shell.FreeTextQuestions, "（自由記述の質問がありません）", id => _shell.OpenTopicQuestion(id));

            AddRow(nav, NavButton(_shell.IsChoiceExpanded ? Icons.Expand : Icons.Collapse, "選択肢別", () => _shell.ToggleChoiceCommand.Execute(null)));
            if (_shell.IsChoiceExpanded)
                AddQuestionSubmenu(nav, _shell.ChoiceQuestions, "（選択肢の質問がありません）", id => _shell.OpenChoiceQuestion(id));
        }

        navScroll.Controls.Add(nav);       // Top + AutoSize inside the scroll host (no filler row needed)
        _sidebar.Controls.Add(navScroll);  // Fill — add first
        _sidebar.Controls.Add(bottom);     // Bottom
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

    // An axis nav row (期間別 / 曜日別 / 地域別): a chevron tracking its expanded state. Clicking it opens the
    // axis summary report and toggles the sub-menu; when expanded, one 質問 sub-item per cross-tab question
    // opens that axis × question cross-tab. The 質問 list is the same for every axis (自由記述 + 選択肢 fields).
    private void AddAxis(TableLayoutPanel nav, string label, bool expanded, Action onClick, AnalysisGrouping axis)
    {
        AddRow(nav, NavButton(expanded ? Icons.Expand : Icons.Collapse, label, onClick));
        if (!expanded)
            return;
        var questions = _shell.CrossTabQuestions;
        if (questions.Count == 0)
            AddRow(nav, SubNavHint("（集計できる質問がありません）"));
        else
            foreach (var (id, name) in questions)
                AddRow(nav, SubNavButton(name, () => _shell.OpenCrossTab(axis, id)));
    }

    // The 質問 sub-items for トピック別 / 選択肢別 — one per question, or a faint hint when there are none.
    private void AddQuestionSubmenu(TableLayoutPanel nav, IReadOnlyList<(long Id, string Name)> questions, string emptyHint, Action<long> open)
    {
        if (questions.Count == 0)
            AddRow(nav, SubNavHint(emptyHint));
        else
            foreach (var (id, name) in questions)
                AddRow(nav, SubNavButton(name, () => open(id)));
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
    // The top margin alone sets the gap above the heading (the item above has no bottom margin), so it is
    // the full ダッシュボード → 切り口 spacing — doubled here at the user's request.
    private Label SectionLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.SectionHeader,
        Font = Theme.Font(8.5f, FontStyle.Bold),
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(LogicalToDeviceUnits(NavTextIndentDip), 24, 0, 4),
    };

    // A faint, indented hint shown in place of sub-nav items when a section has none (e.g. トピック別 with
    // no 自由記述 questions). Indented to line up with the sub-nav item text.
    private Label SubNavHint(string text) => new()
    {
        Text = text,
        ForeColor = Theme.SectionHeader,
        Font = Theme.Font(8.5f),
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(LogicalToDeviceUnits(28), LogicalToDeviceUnits(2), 0, 0),
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
        RefineCsvWithLlm(vm);

        using var form = new ProjectDesignForm(vm);
        if (form.ShowDialog(this) == DialogResult.OK && form.ResultProject is { } project && vm.SourceCsv is { } csv)
        {
            // Persist first (the analyzer reads the saved responses), auto-generate each 自由記述 column's
            // topics from the imported answers, run the import analysis (which now has topics to assign),
            // then rebuild the star and open the dashboard.
            _shell.PersistImportedProject(project, csv, Path.GetFileName(picker.FileName));
            GenerateTopicsForNewProject(project);
            RunImportAnalysis(project);
            _shell.ShowImportedDashboard(project);
        }
    }

    // Runs the import-time sentiment / topic analysis with a modal progress dialog. Skipped (sentiment /
    // topics stay unanalysed) when no API key is configured or the project has no 自由記述 columns. The
    // results are persisted; ShowImportedDashboard's Rebuild then projects them into the star.
    private void RunImportAnalysis(Project project)
    {
        var settings = _shell.CreateSettingsViewModel();
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || !ImportAnalyzer.HasAnalyzableFields(project))
            return;

        var analyzer = new ImportAnalyzer(AppServices.Llm, AppServices.Responses, AppServices.Topics, AppServices.AnalysisResults, settings.SentimentModel);
        using var dialog = new AnalyzeProgressForm((progress, ct) => analyzer.AnalyzeAsync(project, progress, ct));
        dialog.ShowDialog(this);
    }

    // Auto-generates each 自由記述 column's topic dictionary by clustering its imported answers, so the
    // import analysis has topics to assign (a freshly created project has none). One dictionary per column
    // with ≥ 2 distinct answers; the 構成 → 「既存データからトピックを再構築」 button can refine them later.
    // Skipped when no API key (the analysis is skipped too) — same gate as RunImportAnalysis.
    private void GenerateTopicsForNewProject(Project project)
    {
        var settings = _shell.CreateSettingsViewModel();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return;

        var fields = project.Fields
            .Where(f => f.FieldType == FieldType.FreeText && f.Id > 0)
            .Select(f => (Field: f, Answers: AppServices.Responses.LoadValuesForField(f.Id)))
            .Where(x => x.Answers.Distinct().Count() >= 2)
            .ToList();
        if (fields.Count == 0)
            return;

        var clusterer = new TopicClusterer(AppServices.Llm, settings.TopicModel);
        using var dialog = new AnalyzeProgressForm(async (progress, ct) =>
        {
            foreach (var (field, answers) in fields)
            {
                var built = await clusterer.BuildTopicsAsync(answers, progress, ct);
                if (built.Count > 0)
                    AppServices.Topics.ReplaceTopics(field.Id, built.Select(t => (t.Label, (float[]?)t.Centroid)).ToList());
            }
        }, "トピック生成");
        dialog.ShowDialog(this);
    }

    // Refines the heuristic CSV column types AND suggests a project description with the LLM before the
    // design dialog opens, using the column names + a sample of rows as hints. Skipped silently when no
    // API key is configured, and any failure (offline, bad key, unparseable reply) leaves the heuristic
    // types and the empty description in place. Runs on a background thread (off the UI sync context, to
    // avoid a deadlock) while the busy cursor is shown — the UI thread blocks here for the one request.
    private void RefineCsvWithLlm(ProjectDesignViewModel vm)
    {
        if (vm.SourceCsv is not { Header.Count: > 0 } csv)
            return;
        var settings = _shell.CreateSettingsViewModel();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return;

        var previousCursor = Cursor.Current;
        try
        {
            UseWaitCursor = true;
            Cursor.Current = Cursors.WaitCursor;
            var inference = new LlmCsvInference(AppServices.Llm, settings.TopicModel);
            var samples = csv.Rows.Take(20).ToArray();
            var result = Task.Run(() => inference.InferAsync(csv.Header, samples, vm.ProjectDescription)).GetAwaiter().GetResult();
            vm.ApplyInferredTypes(result.Types);
            if (!string.IsNullOrWhiteSpace(result.Description) && string.IsNullOrWhiteSpace(vm.ProjectDescription))
                vm.ProjectDescription = result.Description;
        }
        catch
        {
            // Keep the heuristic types / empty description on any failure.
        }
        finally
        {
            UseWaitCursor = false;
            Cursor.Current = previousCursor;
        }
    }

    // インポート（モーダル）。CSVを取り込み、回答にマージする。
    private void OnImport(Project project)
    {
        using var form = new ImportForm(new ImportViewModel(project, AppServices.Responses, AppServices.Analytics));
        form.ShowDialog(this);
    }

    // ===== 画像の読み込み（ファイル選択 / フォルダ選択 → OCR → 仮テーブル → 校正 → 回答） =====
    //
    // 二つの入口（選んだ画像ファイル群 / 選んだフォルダ直下の全画像）が、同じ共通処理 RunImageImport に
    // 画像パスの一覧を渡す。読み取りは vision OCR で「仮テーブル」(image_import_staging) に貯め、校正画面で
    // 画像と読み取り値を見比べ、レコードごとに「取り込む」で初めて responses へ確定。確定があれば CSV と
    // 同じ感情/トピック解析 → スター再投影 → ダッシュボードを行う。確定するまで実データには一切入らない。

    private static readonly string[] ScanImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    // 画像を読み込む。画像ファイル（複数可）を直接選ばせる。前回のフォルダを既定にし、選んだ場所を記憶する。
    private void OnImportImages(Project project)
    {
        if (!TryGetImageSettings(out var settings))
            return;

        List<string> images;
        using (var picker = new OpenFileDialog
        {
            Title = "読み取る画像を選択（複数選択できます）",
            Multiselect = true,
            Filter = "画像ファイル (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|すべてのファイル (*.*)|*.*",
            InitialDirectory = Directory.Exists(settings.ScanFolderPath) ? settings.ScanFolderPath : "",
        })
        {
            if (picker.ShowDialog(this) == DialogResult.OK)
                images = picker.FileNames.ToList();
            else if (AppServices.ImageStaging.CountForProject(project.Id) > 0)
                images = new List<string>();   // nothing newly picked — resume the staged batch in review
            else
                return;
        }

        // Remember the folder of the picked files as next time's default.
        if (images.Count > 0)
            settings.ScanFolderPath = Path.GetDirectoryName(images[0]) ?? settings.ScanFolderPath;

        RunImageImport(project, settings, images);
    }

    // フォルダから画像を読み込む。フォルダを選ばせ、その直下の画像をすべて読み取る。
    private void OnImportImagesFromFolder(Project project)
    {
        if (!TryGetImageSettings(out var settings))
            return;

        List<string> images;
        using (var picker = new FolderBrowserDialog { Description = "読み取る画像のあるフォルダを選択", SelectedPath = settings.ScanFolderPath, UseDescriptionForTitle = true })
        {
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                settings.ScanFolderPath = picker.SelectedPath;
                images = EnumerateScanImages(picker.SelectedPath);
                if (images.Count == 0 && AppServices.ImageStaging.CountForProject(project.Id) == 0)
                {
                    MessageBox.Show(this, $"フォルダに画像が見つかりませんでした。\n{picker.SelectedPath}",
                        "フォルダから画像を読み込む", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            else if (AppServices.ImageStaging.CountForProject(project.Id) > 0)
                images = new List<string>();   // nothing newly picked — resume the staged batch in review
            else
                return;
        }

        RunImageImport(project, settings, images);
    }

    // OCR は vision モデル必須。API キーが無ければ何もできないので案内して false を返す。
    private bool TryGetImageSettings(out SettingsViewModel settings)
    {
        settings = _shell.CreateSettingsViewModel();
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            return true;
        MessageBox.Show(this, "画像の読み取りには OpenAI の API キーが必要です。設定の「LLM」で設定してください。",
            "画像の読み込み", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    // 共通処理：選ばれた画像（ファイル選択でもフォルダ選択でも同じ）を OCR→仮テーブル→校正→解析する。
    // images が空のときは新規読み取りを行わず、未校正のまま残っている仮バッチをそのまま校正画面に出す。
    private void RunImageImport(Project project, SettingsViewModel settings, List<string> images)
    {
        settings.Save();   // remember the last-used location

        // OCR any newly picked images into the staging table (each is a paid vision request — confirm the
        // batch first). Skipped when the pick added no images (e.g. resuming a previous, unreviewed batch).
        if (images.Count > 0)
        {
            var confirm = MessageBox.Show(this,
                $"{images.Count} 件の画像を読み取ります。読み取り後、校正画面で確認してから取り込みます。よろしいですか？",
                "画像の読み込み", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK)
                return;

            var fields = project.Fields.ToList();
            var choiceOptions = LoadChoiceOptions(fields);
            var extractor = new OcrExtractor(AppServices.Llm, settings.OcrModel);
            using var dialog = new AnalyzeProgressForm((progress, ct) =>
                OcrImagesToStagingAsync(extractor, fields, project.Description, choiceOptions, project.Id, images, progress, ct), "読み取り");
            dialog.ShowDialog(this);   // partial progress is fine — whatever was read is staged for review
        }

        // Review every staged record (newly OCR'd plus any left over). Only 取り込む commits to responses.
        var staged = AppServices.ImageStaging.ListForProject(project.Id);
        if (staged.Count == 0)
        {
            MessageBox.Show(this, "校正する読み取り結果がありませんでした。", "画像の読み込み",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var review = new ImageReviewForm(project, staged, AppServices.Responses, AppServices.ImageStaging);
        review.ShowDialog(this);

        // If the user confirmed any records, run the same sentiment/topic analysis the CSV paths run, then
        // re-project the star and open the dashboard.
        if (review.CommittedCount > 0)
        {
            RunImportAnalysis(project);
            _shell.ShowImportedDashboard(project);
        }
    }

    // Image files directly under the chosen folder, sorted by name for a stable processing order.
    private static List<string> EnumerateScanImages(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => ScanImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

    // The known 選択肢 options per choice field name, gathered from the project's existing answers, used as
    // an OCR hint so the model picks from the exact labels. A field with no recorded options is omitted.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadChoiceOptions(IReadOnlyList<DataField> fields)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var field in fields)
            if (field.FieldType == FieldType.Choice && field.Id > 0)
            {
                var options = AppServices.Responses.LoadChoiceOptions(field.Id);
                if (options.Count > 0)
                    map[field.Name] = options;
            }
        return map;
    }

    // Reads + OCR-extracts each image in turn (reporting n/total) and stages the result — image bytes plus
    // the read values — without touching responses. Bytes are read off the UI thread; the vision call
    // awaits off-thread. A read/OCR error propagates so the dialog reports it; images read before it are
    // already staged and can be reviewed. The LLM cache makes re-reading the same image free.
    private static async Task OcrImagesToStagingAsync(
        OcrExtractor extractor, IReadOnlyList<DataField> fields, string description,
        IReadOnlyDictionary<string, IReadOnlyList<string>> choiceOptions, long projectId,
        IReadOnlyList<string> imagePaths, IProgress<(int Done, int Total)> progress, CancellationToken ct)
    {
        var total = imagePaths.Count;
        var done = 0;
        foreach (var path in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await Task.Run(() => File.ReadAllBytes(path), ct).ConfigureAwait(false);
            var mediaType = MediaTypeFor(path);
            // Split the form into overlapping bands and OCR each: a short band is enlarged by the vision API
            // (instead of the whole page being downsampled), so checkbox ticks read far more reliably. The
            // per-band readings are merged; the original bytes are staged for the human review screen.
            var bands = ImageTiler.ToBands(raw, mediaType);
            var bandResults = new List<IReadOnlyDictionary<string, string>>(bands.Count);
            foreach (var band in bands)
            {
                ct.ThrowIfCancellationRequested();
                bandResults.Add(await extractor.ExtractAsync(band.Bytes, band.MediaType, fields, description, choiceOptions, ct).ConfigureAwait(false));
            }
            var values = OcrExtractor.MergeValues(bandResults, fields);
            AppServices.ImageStaging.Add(projectId, Path.GetFileName(path), mediaType, raw, values);
            done++;
            progress.Report((done, total));
        }
    }

    // The data-URL media type for an image path (defaults to JPEG for .jpg/.jpeg and anything unexpected).
    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // 設定（モーダル）。開くときに保存値を読み込み、閉じたら書き戻す。
    private void OnSettings()
    {
        var viewModel = _shell.CreateSettingsViewModel();
        using var form = new SettingsForm(viewModel);
        form.ShowDialog(this);
        viewModel.Save();
    }

    // エクスポート → 月次レポート（PDF）を発行。対象月を選び、その月の集計（KPI・感情分布・トピック別件数）を
    // QuestPDF で1枚にまとめ、ユーザーが選んだ場所に保存してから開くか尋ねる。
    private void OnExport()
    {
        if (_shell.CurrentProject is not { } project)
            return;

        var months = AppServices.Analytics.AvailableMonths(project.Id);
        if (months.Count == 0)
        {
            MessageBox.Show(this, "月次レポートを発行できる回答がありません。先に回答を取り込んでください。",
                "月次レポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (PromptForMonth(months) is not { } picked)
            return;
        var (year, month) = picked;

        var company = new SettingsViewModel(AppServices.Settings).CompanyName;
        var data = MonthlyReportBuilder.Build(AppServices.Analytics, project, company, year, month);

        static string Safe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "PDF ファイル (*.pdf)|*.pdf",
            FileName = $"{Safe(project.Name)}_月次レポート_{year}年{month}月.pdf",
            AddExtension = true,
            DefaultExt = "pdf",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            MonthlyReportPdf.Save(data, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PDF の生成に失敗しました。\n{ex.Message}", "月次レポート", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (MessageBox.Show(this, "月次レポートを発行しました。開きますか？", "月次レポート", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
    }

    // A small month picker for the 月次レポート: a dropdown of the months that have data (newest first), with
    // 発行 / キャンセル. Returns the chosen (year, month), or null on cancel.
    private (int Year, int Month)? PromptForMonth(IReadOnlyList<(int Year, int Month)> months)
    {
        using var form = new Form
        {
            Text = "月次レポートの発行", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = Theme.Font(),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F),
            BackColor = Theme.ContentBack,
            ClientSize = new Size(LogicalToDeviceUnits(340), LogicalToDeviceUnits(150)),
        };

        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Font(10f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };
        foreach (var (y, m) in months)
            combo.Items.Add($"{y}年{m}月");
        combo.SelectedIndex = 0;   // newest first

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 16, 16, 0), BackColor = Theme.ContentBack };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label { Text = "対象月を選んでください。", AutoSize = true, ForeColor = Theme.BodyText, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        content.Controls.Add(combo, 0, 1);

        var ok = new Button { Text = "発行", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(9.5f, FontStyle.Bold), Padding = new Padding(16, 6, 16, 6), Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Padding = new Padding(12, 6, 12, 6), Cursor = Cursors.Hand };
        cancel.FlatAppearance.BorderColor = Theme.CardBorder;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Padding = new Padding(16, 8, 16, 12) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        form.Controls.Add(content);   // Fill — add first so it yields the bottom strip
        form.Controls.Add(buttons);   // Bottom
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(this) == DialogResult.OK && combo.SelectedIndex >= 0 ? months[combo.SelectedIndex] : null;
    }

    // Placeholder for a dialog/screen not yet migrated, so navigation never dead-ends during the port.
    private void ShowPending(string what) =>
        MessageBox.Show(this, $"「{what}」は WinForms 版に移植中です。", "移植中",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
}
