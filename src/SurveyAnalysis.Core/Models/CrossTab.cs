namespace SurveyAnalysis.Models;

// Which secondary dimension supplies a cross-tab report's columns. A 軸 report (時間別 / 曜日別 / 地域別)
// supplies the rows; choosing a 質問 from its sub-menu pivots the responses into one column per category
// of that question — the topics assigned to a 自由記述 question, or the options of a 選択肢 question (a
// multi-select cell "A; B" counts under both A and B). Plain 軸 reports (no question) have no CrossTabSpec.
public enum CrossTabKind
{
    Topic,   // columns = the question's トピック (fact_response_topic for this field), unset = （未分析）
    Choice,  // columns = the question's 選択肢 options (fact_response_choice for this field), unset = （未選択）
}

// The question chosen for a cross-tab's columns: its kind, its field id (the join target), and its name
// (the report title). Built by the shell from a project field and passed to AnalyticsRepository.
public sealed record CrossTabSpec(CrossTabKind Kind, long FieldId, string Name);
