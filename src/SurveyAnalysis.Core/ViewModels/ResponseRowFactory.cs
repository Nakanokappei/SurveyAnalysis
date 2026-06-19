using System;
using System.Collections.Generic;
using System.Globalization;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// Builds a 個票一覧 row (記入日 / トピック / 感情 / 抜粋) for the drill-down terminals. The excerpt is the
// free-text field only; personal information is never surfaced. Shared by the period / weekday / region /
// topic views so the row shape and date handling live in one place.
internal static class ResponseRowFactory
{
    private static readonly string[] DateFormats =
        { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyy年M月d日" };

    // From a response's analysis: 記入日 + excerpt as below, plus the assigned main topic and the row
    // sentiment (so every drill-down terminal shows トピック / 感情, not "—").
    public static SurveyRow Build(string? dateField, string? excerptField, ResponseAnalysis response)
    {
        var sentiment = response.SentimentScore is { } score ? score.ToString("+0.00;-0.00;0.00") : "—";
        return Build(dateField, excerptField, response.Values, response.Topic ?? "—", sentiment);
    }

    // From a plain field→value map, with topic / sentiment supplied by the caller (defaulting to "—").
    public static SurveyRow Build(string? dateField, string? excerptField, IReadOnlyDictionary<string, string> response, string topic = "—", string sentiment = "—")
    {
        var entryDate = "—";
        if (dateField is not null && response.TryGetValue(dateField, out var date))
            entryDate = FormatDate(date);

        var excerpt = "";
        if (excerptField is not null && response.TryGetValue(excerptField, out var text))
            excerpt = Truncate(text, 40);

        return new SurveyRow { EntryDate = entryDate, Topic = topic, Sentiment = sentiment, Excerpt = excerpt };
    }

    private static string FormatDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        || DateTime.TryParseExact((value ?? "").Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
            ? d.ToString("yyyy/MM/dd")
            : value ?? "—";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
