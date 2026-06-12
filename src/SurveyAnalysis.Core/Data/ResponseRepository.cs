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

    // Loads every response for a project as a field-name→value map (newest first). Multiple answers
    // for the same field keep the last value. Used by the dashboard to aggregate real data.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> LoadForProject(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, a.field_name, a.value
            FROM responses r
            LEFT JOIN answers a ON a.response_id = r.id
            WHERE r.project_id = $pid
            ORDER BY r.id DESC;
            """;
        command.Parameters.AddWithValue("$pid", projectId);

        var byResponse = new Dictionary<long, Dictionary<string, string>>();
        var order = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var responseId = reader.GetInt64(0);
            if (!byResponse.TryGetValue(responseId, out var values))
            {
                values = new Dictionary<string, string>();
                byResponse[responseId] = values;
                order.Add(responseId);
            }
            // LEFT JOIN: a response with no answers yields a null field_name — keep its empty map.
            if (!reader.IsDBNull(1))
                values[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }

        var result = new List<IReadOnlyDictionary<string, string>>(order.Count);
        foreach (var responseId in order)
            result.Add(byResponse[responseId]);
        return result;
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
