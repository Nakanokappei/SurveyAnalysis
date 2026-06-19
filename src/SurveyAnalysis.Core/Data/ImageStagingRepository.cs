using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SurveyAnalysis.Data;

// One OCR'd image waiting to be reviewed: the image bytes (so the review screen shows the picture without
// the source file), and the read 項目名→値 map. Belongs to a project until the user confirms or discards it.
public sealed record StagedImage(
    long Id,
    string SourceName,
    string MediaType,
    byte[] ImageBytes,
    IReadOnlyDictionary<string, string> Values);

// The image-OCR staging area (the 仮テーブル). Image imports land here first — one row per scanned image
// with its bytes and OCR'd values — and stay until the user reviews them against the picture and either
// confirms (the host builds a response from the values, then deletes the staging row) or discards them.
// Nothing reaches the real responses table until the user confirms, so a half-reviewed batch is safe to
// leave: the rows persist (and survive an app restart) until acted on.
public sealed class ImageStagingRepository
{
    private readonly AppDatabase _db;

    public ImageStagingRepository(AppDatabase db) => _db = db;

    // Stages one OCR'd image; returns its new staging-row id.
    public long Add(long projectId, string sourceName, string mediaType, byte[] imageBytes, IReadOnlyDictionary<string, string> values)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO image_import_staging (project_id, source_name, media_type, image_bytes, values_json, created_utc)
            VALUES ($pid, $name, $mt, $bytes, $vals, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$pid", projectId);
        command.Parameters.AddWithValue("$name", sourceName);
        command.Parameters.AddWithValue("$mt", mediaType);
        command.Parameters.AddWithValue("$bytes", imageBytes);
        command.Parameters.AddWithValue("$vals", JsonSerializer.Serialize(values));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return (long)command.ExecuteScalar()!;
    }

    // Every staged image for a project (oldest first), each with its decoded values map. Used to populate
    // the review screen, including any rows left over from a previous, partly-reviewed batch.
    public IReadOnlyList<StagedImage> ListForProject(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_name, media_type, image_bytes, values_json
            FROM image_import_staging
            WHERE project_id = $pid
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$pid", projectId);

        var list = new List<StagedImage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = Deserialize(reader.GetString(4));
            list.Add(new StagedImage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (byte[])reader.GetValue(3), values));
        }
        return list;
    }

    // Removes one staged row — called after the user confirms it (the response is inserted separately) or
    // discards it.
    public void Delete(long id)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM image_import_staging WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // How many images are still staged for a project (lets the host offer to resume a pending review).
    public int CountForProject(long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM image_import_staging WHERE project_id = $pid;";
        command.Parameters.AddWithValue("$pid", projectId);
        return (int)(long)command.ExecuteScalar()!;
    }

    private static IReadOnlyDictionary<string, string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
