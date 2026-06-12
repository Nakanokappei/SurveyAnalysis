using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The application shell. Owns the currently open project (null = no project) and the
// page shown in the right content pane. Every sidebar action is a navigation command
// that swaps CurrentPage; the sidebar itself reshapes on IsProjectOpen.
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectOpen))]
    private Project? _currentProject;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    // True once a project is open; drives which sidemenu buttons are visible.
    public bool IsProjectOpen => CurrentProject is not null;

    // Raised when the user asks to create a project; the view opens the modal design dialog.
    public event Action? CreateProjectRequested;

    // Raised when the user asks to import CSV; the view opens the modal import dialog.
    public event Action<Project>? ImportRequested;

    // Raised when the user asks to edit the open project's schema (データ項目); the view opens the
    // design dialog in edit mode with the current project.
    public event Action<Project>? EditSchemaRequested;

    private readonly ProjectRepository _projects;
    private readonly SettingsRepository _settings;
    private readonly ResponseRepository _responses;

    public MainWindowViewModel(ProjectRepository projects, SettingsRepository settings, ResponseRepository responses)
    {
        _projects = projects;
        _settings = settings;
        _responses = responses;
        // Start with no project open: the welcome page and the "プロジェクトを作る" CTA.
        _currentPage = new WelcomeViewModel(this);
    }

    // Parameterless overload for the XAML design-time DataContext; routes to the same repositories
    // the running app uses (see AppServices). Not used by the live app.
    public MainWindowViewModel() : this(AppServices.Projects, AppServices.Settings, AppServices.Responses) { }

    // Saved projects for the welcome screen's reopen list.
    public IReadOnlyList<Models.ProjectSummary> GetProjectSummaries() => _projects.ListSummaries();

    // Builds a settings view model bound to persistent storage (loads on construct; the dialog
    // host calls Save when it closes).
    public SettingsViewModel CreateSettingsViewModel() => new(_settings);

    // Loads a saved project by id and shows its dashboard.
    public void OpenProject(long id)
    {
        if (_projects.Load(id) is not { } project)
            return;
        CurrentProject = project;
        CurrentPage = new DashboardViewModel(project, _responses, project.Months.Count > 0 ? project.Months[0] : "（今月）");
    }

    // 新規にプロジェクトを作る → the view shows the modal design dialog.
    [RelayCommand]
    private void CreateProject() => CreateProjectRequested?.Invoke();

    // Opens a pre-filled sample project so the dashboard can be reviewed without the
    // full design + scan flow. Prototype convenience only.
    [RelayCommand]
    private void OpenSampleProject()
    {
        CurrentProject = SampleData.CreateSampleProject();
        CurrentPage = new DashboardViewModel(CurrentProject, _responses, CurrentProject.Months[0]);
    }

    // Called by the design screen when the user confirms the new project: persist it, then open
    // its dashboard.
    public void FinishProjectCreation(Project project)
    {
        if (project.Months.Count == 0)
            project.Months.Add("（今月）");
        _projects.Insert(project);
        CurrentProject = project;
        CurrentPage = new DashboardViewModel(project, _responses, project.Months[0]);
    }

    // データ項目（開いているプロジェクトのスキーマを確認・変更するモーダル）
    [RelayCommand]
    private void EditSchema()
    {
        if (CurrentProject is { } project)
            EditSchemaRequested?.Invoke(project);
    }

    // Called by the design dialog (edit mode) when the user saves: persist the new schema, reload
    // the project from storage, and refresh the open project + dashboard.
    public void ApplySchemaEdit(Project draft)
    {
        _projects.Update(draft);
        if (_projects.Load(draft.Id) is not { } reloaded)
            return;
        CurrentProject = reloaded;
        CurrentPage = new DashboardViewModel(reloaded, _responses, reloaded.Months.Count > 0 ? reloaded.Months[0] : "（今月）");
    }

    // ダッシュボード（選択月の集計）
    [RelayCommand]
    private void OpenDashboard()
    {
        if (CurrentProject is { } project)
            CurrentPage = new DashboardViewModel(project, _responses, project.Months[0]);
    }

    // 月次レポート（サイドメニューの月リンク）
    [RelayCommand]
    private void OpenMonthlyReport(string month)
    {
        if (CurrentProject is { } project)
            CurrentPage = new MonthlyReportViewModel(project, month);
    }

    // インポート（モーダルダイアログでCSVをマージ）
    [RelayCommand]
    private void Import()
    {
        if (CurrentProject is { } project)
            ImportRequested?.Invoke(project);
    }

    // プロジェクトを閉じる → back to the welcome page.
    [RelayCommand]
    private void CloseProject()
    {
        CurrentProject = null;
        CurrentPage = new WelcomeViewModel(this);
    }
}
