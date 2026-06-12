using CommunityToolkit.Mvvm.Input;

namespace SurveyAnalysis.ViewModels;

// Shown when no project is open. Offers the same "プロジェクトを作る" entry point as the
// sidebar, plus a shortcut to open the bundled sample project for layout review.
public partial class WelcomeViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _shell;

    public WelcomeViewModel(MainWindowViewModel shell) => _shell = shell;

    [RelayCommand]
    private void CreateProject() => _shell.CreateProjectCommand.Execute(null);

    [RelayCommand]
    private void OpenSample() => _shell.OpenSampleProjectCommand.Execute(null);
}
