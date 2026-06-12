using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public MainWindowViewModel()
    {
        // Start with no project open: the welcome page and the "プロジェクトを作る" CTA.
        _currentPage = new WelcomeViewModel(this);
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
        CurrentPage = new DashboardViewModel(CurrentProject, CurrentProject.Months[0]);
    }

    // Called by the design screen when the user confirms the new project.
    public void FinishProjectCreation(Project project)
    {
        if (project.Months.Count == 0)
            project.Months.Add("（今月）");
        CurrentProject = project;
        CurrentPage = new DashboardViewModel(project, project.Months[0]);
    }

    // ダッシュボード（選択月の集計）
    [RelayCommand]
    private void OpenDashboard()
    {
        if (CurrentProject is { } project)
            CurrentPage = new DashboardViewModel(project, project.Months[0]);
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
