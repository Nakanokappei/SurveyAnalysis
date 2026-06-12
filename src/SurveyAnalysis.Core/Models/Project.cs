using System.Collections.ObjectModel;

namespace SurveyAnalysis.Models;

// A survey project (プロジェクト): a named column definition plus the months that have
// been collected. In this prototype it carries only what the screens need to render.
public class Project
{
    // Database row id. 0 until the project has been saved (the repository assigns it on insert).
    public long Id { get; set; }

    public required string Name { get; init; }

    // The field definitions designed in the project creation screen (データ項目).
    public ObservableCollection<DataField> Fields { get; } = new();

    // Month labels shown as monthly-report links in the sidebar, newest first.
    public ObservableCollection<string> Months { get; } = new();
}
