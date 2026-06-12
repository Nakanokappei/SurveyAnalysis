using System;
using System.Collections.Generic;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Reads and writes imported survey responses and their answers. Answers link to project fields by
// name (field_name), not by fields.id, so that a later schema edit — which deletes and reinserts
// the field rows — does not cascade-delete the responses. Responses still belong to a project, so
// deleting the project removes them.
public sealed class ResponseRepository
{
    private readonly AppDatabase _db;

    public ResponseRepository(AppDatabase db) => _db = db;

    // Persists imported responses (each with its answers) for a project in one transaction.
    public void InsertResponses(long projectId, string source, IReadOnlyList<SurveyResponse> responses)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("o");

        foreach (var response in responses)
        {
            long responseId;
            using (var insertResponse = connection.CreateCommand())
            {
                insertResponse.Transaction = transaction;
                insertResponse.CommandText = """
                    INSERT INTO responses (project_id, source, imported_utc) VALUES ($pid, $src, $now);
                    SELECT last_insert_rowid();
                    """;
                insertResponse.Parameters.AddWithValue("$pid", projectId);
                insertResponse.Parameters.AddWithValue("$src", source);
                insertResponse.Parameters.AddWithValue("$now", now);
                responseId = (long)insertResponse.ExecuteScalar()!;
            }

            foreach (var answer in response.Answers)
            {
                using var insertAnswer = connection.CreateCommand();
                insertAnswer.Transaction = transaction;
                insertAnswer.CommandText = "INSERT INTO answers (response_id, field_name, value) VALUES ($rid, $field, $value);";
                insertAnswer.Parameters.AddWithValue("$rid", responseId);
                insertAnswer.Parameters.AddWithValue("$field", answer.FieldName);
                insertAnswer.Parameters.AddWithValue("$value", answer.Value);
                insertAnswer.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // Total responses stored for a project (shown as import feedback; the dashboard will use this).
    public int CountForProject(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM responses WHERE project_id = $pid;";
        command.Parameters.AddWithValue("$pid", projectId);
        return (int)(long)command.ExecuteScalar()!;
    }
}
