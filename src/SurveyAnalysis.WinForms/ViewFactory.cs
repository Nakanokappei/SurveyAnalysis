using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// Maps a page view model to its WinForms view — the counterpart of Avalonia's ViewLocator. Screens
// not yet migrated fall back to a placeholder so the shell's navigation works throughout the port.
internal static class ViewFactory
{
    public static UserControl Create(ViewModelBase page) => page switch
    {
        WelcomeViewModel vm => new WelcomeControl(vm),
        DashboardViewModel vm => new DashboardControl(vm),
        TimeSliceViewModel vm => new TimeSliceControl(vm),
        _ => new PlaceholderControl(page),
    };
}
