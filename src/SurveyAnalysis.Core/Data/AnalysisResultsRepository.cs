using System;
using SurveyAnalysis.Data;

namespace SurveyAnalysis.Data;

// Writes the import-time LLM analysis (raw, source of truth): a response's row-level sentiment, and per
// 自由記述 column its assigned topic + that column's sentiment. The derived star is projected from these
// by AnalyticsRepository.Rebuild (which never calls the LLM). Upserts, so re-analysing replaces a row.
public sealed class AnalysisResultsRepository
{
    private readonly AppDatabase _db;

    public AnalysisResultsRepository(AppDatabase db) => _db = db;

    // The row-level sentiment over all 自由記述 of a response (the fact measure).
    public void SaveRowSentiment(long responseId, double? score, bool? isNegative)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO response_sentiment (response_id, score, is_negative) VALUES ($r, $s, $n)
            ON CONFLICT(response_id) DO UPDATE SET score = $s, is_negative = $n;
            """;
        command.Parameters.AddWithValue("$r", responseId);
        command.Parameters.AddWithValue("$s", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("$n", isNegative is null ? DBNull.Value : isNegative.Value ? 1 : 0);
        command.ExecuteNonQuery();
    }

    // A 自由記述 column's assigned topic (field_topics.id, or null) + that column's sentiment.
    public void SaveTopicAssignment(long responseId, long fieldId, long? topicId, double? score, bool? isNegative)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO response_topic (response_id, field_id, topic_id, score, is_negative) VALUES ($r, $f, $t, $s, $n)
            ON CONFLICT(response_id, field_id) DO UPDATE SET topic_id = $t, score = $s, is_negative = $n;
            """;
        command.Parameters.AddWithValue("$r", responseId);
        command.Parameters.AddWithValue("$f", fieldId);
        command.Parameters.AddWithValue("$t", (object?)topicId ?? DBNull.Value);
        command.Parameters.AddWithValue("$s", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("$n", isNegative is null ? DBNull.Value : isNegative.Value ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
