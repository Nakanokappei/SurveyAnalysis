using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SurveyAnalysis.Behaviors;

// Attach RequiredField.IsRequired="True" to a TextBox to flag it as a required field.
// When focus leaves the box while it is empty, the "invalid" style class is applied
// (red border + red text via App.axaml). The class is cleared while the box is focused,
// so the warning only appears once the user has left a still-empty required field.
public static class RequiredField
{
    public static readonly AttachedProperty<bool> IsRequiredProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsRequired", typeof(RequiredField));

    public static void SetIsRequired(Control control, bool value) => control.SetValue(IsRequiredProperty, value);
    public static bool GetIsRequired(Control control) => control.GetValue(IsRequiredProperty);

    static RequiredField()
    {
        IsRequiredProperty.Changed.AddClassHandler<TextBox>((box, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                box.LostFocus += OnLostFocus;
                box.GotFocus += OnGotFocus;
            }
            else
            {
                box.LostFocus -= OnLostFocus;
                box.GotFocus -= OnGotFocus;
            }
        });
    }

    // On blur: mark invalid when the required box was left empty.
    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            SetInvalid(box, string.IsNullOrWhiteSpace(box.Text));
    }

    // While editing, never show the warning colour.
    private static void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            SetInvalid(box, false);
    }

    private static void SetInvalid(TextBox box, bool invalid)
    {
        if (invalid && !box.Classes.Contains("invalid"))
            box.Classes.Add("invalid");
        else if (!invalid)
            box.Classes.Remove("invalid");
    }
}
