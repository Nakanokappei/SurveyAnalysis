using System;
using Avalonia.Controls;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.Views;

public partial class ProjectDesignWindow : Window
{
    public ProjectDesignWindow() => InitializeComponent();

    // Close the dialog with the created project (or null on cancel) so the opener can react.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ProjectDesignViewModel vm)
        {
            vm.Completed += project => Close(project);
            vm.Cancelled += () => Close(null);
        }
    }
}
