using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace SurveyAnalysis.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    // Toggles masking on the API key field so the user can verify what they pasted.
    private void OnRevealApiKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
            ApiKeyBox.RevealPassword = toggle.IsChecked ?? false;
    }
}
