namespace SurveyAnalysis.Models;

// The grain the 時間別 (time) slice groups by. All grains derive from one fact date_key via the
// dim_date attributes, so switching grain only changes the GROUP BY — the fact table is unchanged.
// Years and quarters follow the Japanese fiscal year (年度, April start), e.g. 2026年度 Q1 = Apr–Jun.
public enum TimeGrain
{
    FiscalYear,     // 年度別   ("2026年度")
    FiscalQuarter,  // 四半期別 ("2026年度 Q1")
    Month,          // 月別     ("2026年5月")
    Week,           // 週別     ("2026年 第21週")
    DayOfWeek       // 曜日別   ("水曜日")
}

public static class TimeGrainInfo
{
    public static string Label(TimeGrain grain) => grain switch
    {
        TimeGrain.FiscalYear => "年度別",
        TimeGrain.FiscalQuarter => "四半期別",
        TimeGrain.Month => "月別",
        TimeGrain.Week => "週別",
        TimeGrain.DayOfWeek => "曜日別",
        _ => grain.ToString()
    };
}
