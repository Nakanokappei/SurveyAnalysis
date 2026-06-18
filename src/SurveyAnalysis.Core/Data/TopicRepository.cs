using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// CRUD for the per-column topic dictionary (field_topics). Topics are managed in the 構成 dialog's
// トピック tab and also (re)built by clustering. Writes are immediate — independent of the staged
// field-schema save — because they hang off a stable field id, not the draft. Centroids are stored as
// float32 BLOBs (same encoding as the LLM embedding cache) for nearest-topic assignment at import time.
public sealed class TopicRepository
{
    private readonly AppDatabase _db;

    public TopicRepository(AppDatabase db) => _db = db;

    // The field's topics, ordered by label.
    public IReadOnlyList<FieldTopic> ListTopics(long fieldId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, field_id, label, centroid FROM field_topics WHERE field_id = $f ORDER BY label;";
        command.Parameters.AddWithValue("$f", fieldId);

        var topics = new List<FieldTopic>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            topics.Add(new FieldTopic
            {
                Id = reader.GetInt64(0),
                FieldId = reader.GetInt64(1),
                Label = reader.GetString(2),
                Centroid = reader.IsDBNull(3) ? null : ToFloats((byte[])reader[3]),
            });
        return topics;
    }

    // Inserts a topic and returns its id. Throws SqliteException on a duplicate label within the field.
    public long AddTopic(long fieldId, string label)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO field_topics (field_id, label, created_utc) VALUES ($f, $l, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$f", fieldId);
        command.Parameters.AddWithValue("$l", label);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return (long)command.ExecuteScalar()!;
    }

    public void RenameTopic(long topicId, string newLabel)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE field_topics SET label = $l WHERE id = $id;";
        command.Parameters.AddWithValue("$l", newLabel);
        command.Parameters.AddWithValue("$id", topicId);
        command.ExecuteNonQuery();
    }

    public void DeleteTopic(long topicId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM field_topics WHERE id = $id;";
        command.Parameters.AddWithValue("$id", topicId);
        command.ExecuteNonQuery();
    }

    // Replaces all of a field's topics in one transaction (used by clustering): deletes the existing set,
    // then inserts the new labels with their centroids.
    public void ReplaceTopics(long fieldId, IReadOnlyList<(string Label, float[]? Centroid)> topics)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM field_topics WHERE field_id = $f;";
            delete.Parameters.AddWithValue("$f", fieldId);
            delete.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow.ToString("o");
        foreach (var (label, centroid) in topics)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO field_topics (field_id, label, centroid, created_utc) VALUES ($f, $l, $c, $now);";
            insert.Parameters.AddWithValue("$f", fieldId);
            insert.Parameters.AddWithValue("$l", label);
            insert.Parameters.AddWithValue("$c", centroid is null ? DBNull.Value : ToBytes(centroid));
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
