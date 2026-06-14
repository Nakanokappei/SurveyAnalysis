using System;

namespace SurveyAnalysis.Models;

// The dashboard's 対象期間 selection. A preset is a window ending today (当日/昨日/直近N日); Custom is a
// user-picked [from, to] range chosen on the calendar. The slices keep their own AggregationPeriod —
// this set (with カスタム期間) is the dashboard's Google-Analytics-style picker.
public enum DateRangePreset
{
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    Last60Days,
    Custom,
}

public static class DateRangePresetInfo
{
    // The presets offered in the picker, in display order (カスタム期間 last).
    public static readonly DateRangePreset[] All =
    {
        DateRangePreset.Today,
        DateRangePreset.Yesterday,
        DateRangePreset.Last7Days,
        DateRangePreset.Last30Days,
        DateRangePreset.Last60Days,
        DateRangePreset.Custom,
    };

    public static string Label(DateRangePreset preset) => preset switch
    {
        DateRangePreset.Today => "今日",
        DateRangePreset.Yesterday => "昨日",
        DateRangePreset.Last7Days => "直近7日",
        DateRangePreset.Last30Days => "直近30日",
        DateRangePreset.Last60Days => "直近60日",
        DateRangePreset.Custom => "カスタム期間",
        _ => preset.ToString(),
    };

    // The inclusive [from, to] range for a preset, anchored at today (date-only). 直近N日 ends yesterday
    // (N days up to yesterday) — today's responses are likely still incomplete. Custom returns null —
    // the caller supplies the user-picked range.
    public static (DateTime From, DateTime To)? Range(DateRangePreset preset, DateTime today)
    {
        var t = today.Date;
        return preset switch
        {
            DateRangePreset.Today => (t, t),
            DateRangePreset.Yesterday => (t.AddDays(-1), t.AddDays(-1)),
            DateRangePreset.Last7Days => (t.AddDays(-7), t.AddDays(-1)),
            DateRangePreset.Last30Days => (t.AddDays(-30), t.AddDays(-1)),
            DateRangePreset.Last60Days => (t.AddDays(-60), t.AddDays(-1)),
            _ => null,
        };
    }
}
