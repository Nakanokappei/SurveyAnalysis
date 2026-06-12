using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace SurveyAnalysis.Data;

// Reads and writes the application settings as a flat key/value table. A key/value shape keeps the
// schema stable as settings are added, and lets secret values be stored already-protected. The
// settings view model owns the mapping between its properties and these keys.
public sealed class SettingsRepository
{
    private readonly AppDatabase _db;

    public SettingsRepository(AppDatabase db) => _db = db;

    // Returns every stored setting. Missing keys are simply absent; the caller falls back to its
    // own defaults.
    public IReadOnlyDictionary<string, string> LoadAll()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";

        var values = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    // Upserts all supplied settings in one transaction.
    public void Save(IReadOnlyDictionary<string, string> values)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
