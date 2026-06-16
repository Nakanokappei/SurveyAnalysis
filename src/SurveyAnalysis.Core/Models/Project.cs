using System.Collections.ObjectModel;

namespace SurveyAnalysis.Models;

// A survey project (プロジェクト): a name plus the field definitions designed for it.
public class Project
{
    // Database row id. 0 until the project has been saved (the repository assigns it on insert).
    public long Id { get; set; }

    public required string Name { get; init; }

    // The field definitions designed in the project creation screen (データ項目).
    public ObservableCollection<DataField> Fields { get; } = new();
}
