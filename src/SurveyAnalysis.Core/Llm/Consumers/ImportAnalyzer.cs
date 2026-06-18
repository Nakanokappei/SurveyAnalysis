using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Llm.Consumers;

// Runs the import-time analysis for a project: per response it scores the row-level sentiment over all
// 自由記述, and per 自由記述 column scores that column's sentiment and assigns it to the nearest topic
// (when the column's dictionary has centroids). Results are persisted to the raw analysis tables, which
// AnalyticsRepository.Rebuild then projects into the star. Reports progress per response and is
// cancellable; the LLM cache makes re-runs cheap.
public sealed class ImportAnalyzer
{
    private readonly SentimentAnalyzer _sentiment;
    private readonly TopicAssigner _assigner;
    private readonly ResponseRepository _responses;
    private readonly TopicRepository _topics;
    private readonly AnalysisResultsRepository _results;

    public ImportAnalyzer(ILlmClient llm, ResponseRepository responses, TopicRepository topics, AnalysisResultsRepository results, string sentimentModel)
    {
        _sentiment = new SentimentAnalyzer(llm, sentimentModel);
        _assigner = new TopicAssigner(llm);
        _responses = responses;
        _topics = topics;
        _results = results;
    }

    // True when the project has any 自由記述 column to analyse (the host can skip the progress UI if not).
    public static bool HasAnalyzableFields(Project project) =>
        project.Fields.Any(f => f.FieldType == FieldType.FreeText && f.Id > 0);

    public async Task AnalyzeAsync(Project project, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var freeTextFields = project.Fields
            .Where(f => f.FieldType == FieldType.FreeText && f.Id > 0)
            .ToList();
        if (freeTextFields.Count == 0)
            return;

        var topicsByField = freeTextFields.ToDictionary(f => f.Id, f => _topics.ListTopics(f.Id));
        var rows = _responses.LoadForProjectWithIds(project.Id);
        var total = rows.Count;
        var done = 0;

        foreach (var (responseId, values) in rows)
        {
            ct.ThrowIfCancellationRequested();

            // The non-empty 自由記述 answers of this response.
            var texts = new List<(long FieldId, string Text)>();
            foreach (var field in freeTextFields)
                if (values.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                    texts.Add((field.Id, value.Trim()));

            if (texts.Count > 0)
            {
                // Row-level sentiment over all 自由記述 combined (the fact measure). With a single column
                // this is the same text as below, so the LLM cache serves the per-column call for free.
                var combined = string.Join("\n", texts.Select(t => t.Text));
                var (rowScore, rowNegative) = await _sentiment.AnalyzeAsync(combined, ct).ConfigureAwait(false);
                _results.SaveRowSentiment(responseId, rowScore, rowNegative);

                // Per column: its own sentiment + nearest topic.
                foreach (var (fieldId, text) in texts)
                {
                    var (score, negative) = await _sentiment.AnalyzeAsync(text, ct).ConfigureAwait(false);
                    var topicId = (await _assigner.AssignAsync(new[] { text }, topicsByField[fieldId], ct).ConfigureAwait(false))[0];
                    _results.SaveTopicAssignment(responseId, fieldId, topicId, score, negative);
                }
            }

            done++;
            progress?.Report((done, total));
        }
    }
}
