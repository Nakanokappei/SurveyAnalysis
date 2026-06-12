using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Shown when no project is open. Lists previously saved projects so they can be reopened, and
// offers the "プロジェクトを作る" entry point plus a shortcut to the bundled sample project.
public partial class WelcomeViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _shell;

    // Saved projects, newest-updated first. Empty on a fresh install.
    public ObservableCollection<ProjectSummary> RecentProjects { get; } = new();

    // Drives whether the "最近のプロジェクト" section is shown.
    public bool HasRecentProjects => RecentProjects.Count > 0;

    public WelcomeViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        foreach (var summary in shell.GetProjectSummaries())
            RecentProjects.Add(summary);
    }

    // 保存済みプロジェクトを開く
    [RelayCommand]
    private void OpenProject(ProjectSummary summary) => _shell.OpenProject(summary.Id);

    [RelayCommand]
    private void CreateProject() => _shell.CreateProjectCommand.Execute(null);

    [RelayCommand]
    private void OpenSample() => _shell.OpenSampleProjectCommand.Execute(null);
}
