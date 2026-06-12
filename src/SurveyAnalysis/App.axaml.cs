using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using SurveyAnalysis.ViewModels;
using SurveyAnalysis.Views;

namespace SurveyAnalysis;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AppServices builds the database (and ensures the schema) on first access.
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(AppServices.Projects, AppServices.Settings),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}