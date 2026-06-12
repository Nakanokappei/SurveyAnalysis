using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SurveyAnalysis.Converters;

// Converts a "#RRGGBB" string (as carried by BarItem.Accent) into a brush so chart bars
// can be tinted from plain data without storing Avalonia types in the model.
public class HexBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string hex && Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
