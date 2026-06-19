using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Reads and writes imported survey responses and their answers. Answers reference their project field
// by id (answers.field_id): the field id is stable across schema edits (ProjectRepository.Update
// updates fields in place), so a rename / retype / reorder of a field does not break the link, while
// removing a field cascade-deletes its answers. Responses belong to a project, so deleting the project
// removes them too. Imported answers arrive keyed by field name (the CSV mapping) and are resolved to
// the field id on insert.
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

        // Map the project's field names to their ids once; answers carry the name and resolve through it.
        var fieldIdByName = LoadFieldIds(connection, transaction, projectId);

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
                // An answer for a name with no matching project field is skipped (e.g. a dropped field).
                if (!fieldIdByName.TryGetValue(answer.FieldName, out var fieldId))
                    continue;
                using var insertAnswer = connection.CreateCommand();
                insertAnswer.Transaction = transaction;
                insertAnswer.CommandText = "INSERT INTO answers (response_id, field_id, value) VALUES ($rid, $fid, $value);";
                insertAnswer.Parameters.AddWithValue("$rid", responseId);
                insertAnswer.Parameters.AddWithValue("$fid", fieldId);
                insertAnswer.Parameters.AddWithValue("$value", answer.Value);
                insertAnswer.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // The project's field name → id map (last wins if names collide). Used to resolve imported answers.
    private static Dictionary<string, long> LoadFieldIds(SqliteConnection connection, SqliteTransaction transaction, long projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, id FROM fields WHERE project_id = $pid;";
        command.Parameters.AddWithValue("$pid", projectId);
        var map = new Dictionary<string, long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetInt64(1);
        return map;
    }

    // Loads every response for a project as a field-name→value map (newest first), the name resolved
    // through fields so a renamed field reads under its current name. Multiple answers for the same
    // field keep the last value. Used by the dashboard to aggregate real data.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> LoadForProject(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, fl.name, a.value
            FROM responses r
            LEFT JOIN answers a ON a.response_id = r.id
            LEFT JOIN fields fl ON fl.id = a.field_id
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

    // Every response for a project as (id, field-name→value map), oldest first. The import analyzer needs
    // the response id (to attach its sentiment / topic results) alongside the answer values.
    public IReadOnlyList<(long Id, IReadOnlyDictionary<string, string> Values)> LoadForProjectWithIds(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, fl.name, a.value
            FROM responses r
            LEFT JOIN answers a ON a.response_id = r.id
            LEFT JOIN fields fl ON fl.id = a.field_id
            WHERE r.project_id = $pid
            ORDER BY r.id ASC;
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
            if (!reader.IsDBNull(1))
                values[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }

        return order.Select(id => (id, (IReadOnlyDictionary<string, string>)byResponse[id])).ToList();
    }

    // Every non-empty answer value stored for one field, oldest first. Topic clustering reads a 自由記述
    // column's answers this way to build its topic dictionary from existing data.
    public IReadOnlyList<string> LoadValuesForField(long fieldId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.value
            FROM answers a
            WHERE a.field_id = $f AND a.value IS NOT NULL AND TRIM(a.value) <> ''
            ORDER BY a.response_id ASC;
            """;
        command.Parameters.AddWithValue("$f", fieldId);

        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    // The distinct 選択肢 options recorded for one field, in first-seen order, split out of the stored
    // multi-select cells (";"-joined). Used as a hint for image OCR so the model picks from the exact
    // option labels this project already uses, rather than paraphrasing the printed choices.
    public IReadOnlyList<string> LoadChoiceOptions(long fieldId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.value
            FROM answers a
            WHERE a.field_id = $f AND a.value IS NOT NULL AND TRIM(a.value) <> ''
            ORDER BY a.response_id ASC;
            """;
        command.Parameters.AddWithValue("$f", fieldId);

        var options = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            foreach (var option in ChoiceValues.Split(reader.GetString(0)))
                if (!options.Contains(option))
                    options.Add(option);
        return options;
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
