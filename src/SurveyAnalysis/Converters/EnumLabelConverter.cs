using System;
using System.Globalization;
using Avalonia.Data.Converters;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Converters;

// Converts a FieldType / AnalysisMethod / AggregationPeriod enum value to its Japanese UI label so
// combo boxes and read-outs show wording that matches the rest of the screen.
public class EnumLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FieldType fieldType => FieldTypeInfo.Label(fieldType),
        AnalysisMethod analysis => FieldTypeInfo.Label(analysis),
        AggregationPeriod period => AggregationPeriodInfo.Label(period),
        _ => value?.ToString()
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
