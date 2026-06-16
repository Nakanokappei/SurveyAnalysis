using System;
using System.Globalization;

namespace SurveyAnalysis.Data;

// Parses the survey date strings that appear in answers and CSV columns — in one place so the ETL,
// the CSV column-type guess, and the dashboard all accept exactly the same formats. A value is a date
// if it parses under the invariant culture or one of the explicit Japanese / ISO formats below;
// otherwise it is not a date. Centralising this avoids the drift where a column the importer accepts
// as 日付 fails to date during aggregation (or vice versa).
public static class DateParsing
{
    // Accepted in addition to the invariant parse: slash / hyphen with zero-padded or bare components,
    // and the Japanese 年月日 form.
    private static readonly string[] DateFormats =
        { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyy年M月d日" };

    // Tries to parse a survey date value (trimmed). Returns false for null/blank/unrecognised input.
    public static bool TryParse(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // Whether a value parses as a date (the importer's column-type guess).
    public static bool IsDate(string value) => TryParse(value, out _);
}
