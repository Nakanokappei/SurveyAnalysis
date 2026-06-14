using System;

namespace SurveyAnalysis.Models;

// A 対象期間 selection shared by the dashboard and every 切り口: a preset window (or a custom [from, to]
// range), the label to show on the picker, and the inclusive date_key window used to filter the facts.
// Centralising it keeps the dashboard and the slices on one picker and one range semantics.
public sealed class DateRangeSelection
{
    public DateRangePreset Preset { get; private set; } = DateRangePreset.Last30Days;
    public DateTime From { get; private set; }
    public DateTime To { get; private set; }

    public DateRangeSelection()
    {
        var (from, to) = DateRangePresetInfo.Range(DateRangePreset.Last30Days, DateTime.Today)!.Value;
        From = from;
        To = to;
    }

    // The picker button's text: the preset's name, or the custom range as dates.
    public string Label => Preset == DateRangePreset.Custom
        ? $"{From:yyyy/MM/dd} 〜 {To:yyyy/MM/dd}"
        : DateRangePresetInfo.Label(Preset);

    public void Set(DateRangePreset preset, DateTime from, DateTime to)
    {
        Preset = preset;
        From = from.Date;
        To = to.Date;
    }

    // Inclusive [from, to] date_key (yyyymmdd) window for the fact filter (slices).
    public (long FromKey, long ToKey) DateKeyWindow => (DateKey(From), DateKey(To));

    private static long DateKey(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
}
