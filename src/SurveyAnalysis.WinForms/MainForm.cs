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

    public MainForm()
    {
        Text = "アンケート分析";
        ClientSize = new Size(1165, 720);
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
        _shell.ImportRequested += _ => ShowPending("インポート (CSV)");
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

        // Bottom actions (docked bottom): import / close while a project is open, then 設定 (app-wide).
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 8, 0, 12),
            BackColor = Theme.SidebarBack,
        };
        if (_shell.IsProjectOpen)
        {
            bottom.Controls.Add(NavButton("＋ インポート (CSV)", () => _shell.ImportCommand.Execute(null)));
            bottom.Controls.Add(NavButton("✕ プロジェクトを閉じる", () => _shell.CloseProjectCommand.Execute(null)));
            bottom.Controls.Add(Divider());
        }
        bottom.Controls.Add(NavButton("⚙ 設定", () => ShowPending("設定")));

        // Main navigation fills the space above the bottom actions.
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.SidebarBack,
            Padding = new Padding(0, 14, 0, 0),
        };

        nav.Controls.Add(new Label
        {
            Text = "プロジェクト",
            ForeColor = Color.White,
            Font = Theme.Font(13.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(16, 4, 16, 2),
        });
        if (_shell.IsProjectOpen && _shell.CurrentProject is { } project)
            nav.Controls.Add(new Label
            {
                Text = project.Name,
                ForeColor = Theme.ProjectName,
                Font = Theme.Font(9f),
                AutoSize = true,
                Margin = new Padding(16, 0, 16, 8),
            });

        if (_shell.IsProjectOpen)
        {
            nav.Controls.Add(NavButton("▤ ダッシュボード", () => _shell.OpenDashboardCommand.Execute(null)));
            nav.Controls.Add(NavButton("✎ データ項目", () => _shell.EditSchemaCommand.Execute(null)));
            nav.Controls.Add(SectionLabel("切り口"));
            nav.Controls.Add(NavButton(_shell.IsTimeExpanded ? "▾ 時間別" : "▸ 時間別", () => _shell.ToggleTimeCommand.Execute(null)));
            if (_shell.IsTimeExpanded)
            {
                nav.Controls.Add(SubNavButton("期間", () => _shell.OpenPeriodCommand.Execute(null)));
                nav.Controls.Add(SubNavButton("曜日", () => _shell.OpenWeekdayCommand.Execute(null)));
            }
            nav.Controls.Add(NavButton("▸ 地域別", () => _shell.OpenSliceCommand.Execute(SliceKind.Region)));
            nav.Controls.Add(NavButton("▸ トピック別", () => _shell.OpenSliceCommand.Execute(SliceKind.Topic)));
        }

        _sidebar.Controls.Add(nav);     // Fill — add first
        _sidebar.Controls.Add(bottom);  // Bottom
        _sidebar.ResumeLayout();
    }

    // A flat, left-aligned sidebar button that highlights on hover, wired to a command.
    private Button NavButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = _sidebar.Width - 24,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SidebarBack,
            ForeColor = Theme.NavText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(10f),
            Margin = new Padding(8, 2, 8, 2),
            Padding = new Padding(10, 0, 0, 0),
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
        button.Padding = new Padding(30, 0, 0, 0);
        button.Height = 32;
        return button;
    }

    // A dim section heading ("切り口") above a group of nav buttons.
    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.SectionHeader,
        Font = Theme.Font(8.5f, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(16, 12, 0, 4),
    };

    // A thin divider line between the project actions and 設定.
    private Panel Divider() => new()
    {
        Height = 1,
        Width = _sidebar.Width - 28,
        BackColor = Theme.SidebarHover,
        Margin = new Padding(14, 8, 14, 8),
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

    // Placeholder for a dialog/screen not yet migrated, so navigation never dead-ends during the port.
    private void ShowPending(string what) =>
        MessageBox.Show(this, $"「{what}」は WinForms 版に移植中です。", "移植中",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
}
