using System.Collections.Generic;

namespace SurveyAnalysis.Models;

// Builds the dummy project used to populate the screens. No data is read from disk or a
// database in this prototype — every value here is placeholder content for layout review.
public static class SampleData
{
    // Creates a fully-populated sample project so the dashboard and sidebar have something
    // to show without going through the design + scan flow.
    public static Project CreateSampleProject()
    {
        var project = new Project { Name = "○○ケーブル 工事アンケート" };

        project.Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name, Analysis = AnalysisMethod.None });
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, Analysis = AnalysisMethod.None, UseForAggregation = true });
        project.Fields.Add(new DataField { Name = "工事内容", FieldType = FieldType.ChoiceText, Analysis = AnalysisMethod.Topic });
        project.Fields.Add(new DataField { Name = "スタッフ対応", FieldType = FieldType.ChoiceText, Analysis = AnalysisMethod.Sentiment });
        project.Fields.Add(new DataField { Name = "ご意見・ご要望", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
        project.Fields.Add(new DataField { Name = "連絡先電話番号", FieldType = FieldType.Phone, Analysis = AnalysisMethod.None });

        foreach (var month in new[] { "2026年5月", "2026年4月", "2026年3月", "2026年2月", "2026年1月", "2025年12月" })
            project.Months.Add(month);

        return project;
    }

    // Topic distribution shown on the dashboard overview.
    public static IReadOnlyList<(string Label, int Count)> TopicCounts { get; } = new[]
    {
        ("配線・接続", 48),
        ("訪問予約・日程", 31),
        ("スタッフ対応", 27),
        ("料金・契約説明", 19),
        ("その他", 12),
    };

    // Sentiment distribution shown on the dashboard overview.
    public static IReadOnlyList<(string Label, int Count, string Accent)> SentimentCounts { get; } = new[]
    {
        ("ポジティブ", 84, "#16A34A"),
        ("中立", 41, "#CA8A04"),
        ("ネガティブ", 12, "#DC2626"),
    };

    // Table rows for the dashboard overview (free-text excerpts only, never PII).
    public static IReadOnlyList<SurveyRow> RecentRows { get; } = new[]
    {
        new SurveyRow { EntryDate = "2026/05/28", Topic = "スタッフ対応", Sentiment = "+0.72", Excerpt = "担当の方が丁寧に説明してくれて安心できました。" },
        new SurveyRow { EntryDate = "2026/05/27", Topic = "配線・接続", Sentiment = "-0.63", Excerpt = "配線が雑で、後日やり直しになった。連絡も遅い。" },
        new SurveyRow { EntryDate = "2026/05/26", Topic = "訪問予約・日程", Sentiment = "-0.12", Excerpt = "予約は取りやすかったが、当日少し遅れて到着した。" },
        new SurveyRow { EntryDate = "2026/05/25", Topic = "料金・契約説明", Sentiment = "-0.55", Excerpt = "料金の説明が分かりにくく、追加費用が後から判明した。" },
        new SurveyRow { EntryDate = "2026/05/24", Topic = "スタッフ対応", Sentiment = "+0.68", Excerpt = "作業前後の養生がきれいで好印象でした。" },
    };
}
