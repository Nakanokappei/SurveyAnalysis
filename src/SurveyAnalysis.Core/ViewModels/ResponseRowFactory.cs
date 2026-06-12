using System;
using System.Collections.Generic;
using System.Globalization;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Builds a 個票一覧 row (記入日 + 抜粋) from a response's field→value map for the drill-down terminals.
// The excerpt is the free-text field only; personal information is never surfaced. Shared by the
// period and weekday views so the row shape and date handling live in one place.
internal static class ResponseRowFactory
{
    private static readonly string[] DateFormats =
        { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyy年M月d日" };

    public static SurveyRow Build(string? dateField, string? excerptField, IReadOnlyDictionary<string, string> response)
    {
        var entryDate = "—";
        if (dateField is not null && response.TryGetValue(dateField, out var date))
            entryDate = FormatDate(date);

        var excerpt = "";
        if (excerptField is not null && response.TryGetValue(excerptField, out var text))
            excerpt = Truncate(text, 40);

        return new SurveyRow { EntryDate = entryDate, Topic = "—", Sentiment = "—", Excerpt = excerpt };
    }

    private static string FormatDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        || DateTime.TryParseExact((value ?? "").Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
            ? d.ToString("yyyy/MM/dd")
            : value ?? "—";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
