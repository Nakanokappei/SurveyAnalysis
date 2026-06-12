using System;

namespace SurveyAnalysis.Models;

// The 集計期間 (rolling date window) shown top-right on every 切り口. The window is the last N days
// ending today; the year options step back by calendar year. Default is 90日. It only narrows which
// responses are counted — it does not change the drill-down hierarchy.
public enum AggregationPeriod
{
    Days7,
    Days14,
    Days30,
    Days90,
    Days180,
    Year1,
    Year2,
}

public static class AggregationPeriodInfo
{
    // Dropdown order, widest first (2年 → 7日), matching how the periods were specified.
    public static readonly AggregationPeriod[] All =
    {
        AggregationPeriod.Year2, AggregationPeriod.Year1, AggregationPeriod.Days180,
        AggregationPeriod.Days90, AggregationPeriod.Days30, AggregationPeriod.Days14, AggregationPeriod.Days7,
    };

    public static string Label(AggregationPeriod period) => period switch
    {
        AggregationPeriod.Year2 => "2年",
        AggregationPeriod.Year1 => "1年",
        AggregationPeriod.Days180 => "180日",
        AggregationPeriod.Days90 => "90日",
        AggregationPeriod.Days30 => "30日",
        AggregationPeriod.Days14 => "14日",
        AggregationPeriod.Days7 => "7日",
        _ => period.ToString(),
    };

    // The inclusive [from, to] date_key (yyyymmdd) window for a period ending on `today`.
    public static (long FromKey, long ToKey) Window(AggregationPeriod period, DateTime today)
    {
        var from = period switch
        {
            AggregationPeriod.Year2 => today.AddYears(-2),
            AggregationPeriod.Year1 => today.AddYears(-1),
            AggregationPeriod.Days180 => today.AddDays(-180),
            AggregationPeriod.Days90 => today.AddDays(-90),
            AggregationPeriod.Days30 => today.AddDays(-30),
            AggregationPeriod.Days14 => today.AddDays(-14),
            AggregationPeriod.Days7 => today.AddDays(-7),
            _ => today.AddDays(-90),
        };
        return (DateKey(from), DateKey(today));
    }

    private static long DateKey(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
}
