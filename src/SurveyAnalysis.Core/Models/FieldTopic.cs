namespace SurveyAnalysis.Models;

// One entry in a 自由記述（FreeText）column's topic dictionary. Topics are scoped to a field, so the
// same label can exist under different columns. The centroid (the cluster centre in embedding space)
// is set when topics are built by clustering and is used to assign new answers to the nearest topic;
// it is null for hand-added topics.
public sealed class FieldTopic
{
    public required long Id { get; init; }
    public required long FieldId { get; init; }
    public required string Label { get; set; }
    public float[]? Centroid { get; init; }
}
