using Avalonia.Controls;

namespace SurveyAnalysis.Views;

// Modal dialog hosting the CSV import flow. Closing is via the window's × (the merge
// action only reports a status message in this prototype), so no result is returned.
public partial class ImportWindow : Window
{
    public ImportWindow() => InitializeComponent();
}
