using System;
using System.Collections.Generic;

namespace SurveyAnalysis.Data;

// A 選択肢 answer can record several selected options in one cell, joined by "; " (multi-select).
// This is the single place that knows that convention: it splits a stored cell into its individual
// options — trimmed, empties dropped, de-duplicated, original order kept — so each option becomes its
// own dim_choice value and fact_response_choice bridge row. A single-select answer (no separator)
// yields exactly one option; a free-text write-in like "その他: チラシを見て" has no ";" so it stays whole.
public static class ChoiceValues
{
    // The separator that joins multiple selected options within one choice cell.
    public const char Separator = ';';

    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var options = new List<string>();
        foreach (var part in value.Split(Separator))
        {
            var option = part.Trim();
            if (option.Length > 0 && !options.Contains(option))
                options.Add(option);
        }
        return options;
    }
}
