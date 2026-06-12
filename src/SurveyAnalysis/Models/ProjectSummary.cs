using System;

namespace SurveyAnalysis.Models;

// A lightweight row for the welcome screen's saved-project list: just enough to identify a
// project and reopen it (the full Project — fields and months — is loaded on demand by id).
public sealed class ProjectSummary
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required DateTime UpdatedUtc { get; init; }
    public required int FieldCount { get; init; }

    // 最終更新日（一覧に表示）。保存は UTC、表示はローカル。
    public string UpdatedDisplay => UpdatedUtc.ToLocalTime().ToString("yyyy/MM/dd");

    // 項目数（例: 「6項目」）。
    public string FieldCountDisplay => $"{FieldCount}項目";
}
