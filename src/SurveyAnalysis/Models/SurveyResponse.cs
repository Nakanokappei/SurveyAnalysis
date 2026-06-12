using System.Collections.Generic;

namespace SurveyAnalysis.Models;

// One imported survey response (one CSV row after the column mapping is applied): the set of
// answers it produced. Answers reference their project field by name (see ResponseRepository for
// why name rather than id).
public sealed class SurveyResponse
{
    public required IReadOnlyList<FieldAnswer> Answers { get; init; }
}

// One answer within a response: the target project field's name and the imported value.
public sealed record FieldAnswer(string FieldName, string Value);
