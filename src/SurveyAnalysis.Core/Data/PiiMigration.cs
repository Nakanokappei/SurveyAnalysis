using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// One-time encryption of PII left in plaintext from before encryption was added (existing answers and any
// staged OCR rows). Idempotent: gated by a settings flag and re-encoding only values not already encrypted,
// so it is safe to run on every startup and a no-op once done. Skipped when the protector is locked (the DB
// was opened by another user / machine — there is nothing this user can encrypt).
public static class PiiMigration
{
    private const string DoneFlag = "pii_migrated";

    public static void EncryptExisting(AppDatabase db, IDataProtector protector)
    {
        if (!protector.IsUnlocked)
            return;

        using var connection = db.Open();
        if (ReadSetting(connection, DoneFlag) == "1")
            return;

        using var transaction = connection.BeginTransaction();
        EncryptAnswers(connection, transaction, protector);
        EncryptStaging(connection, transaction, protector);
        WriteSetting(connection, transaction, DoneFlag, "1");
        transaction.Commit();
    }

    // Encrypts the value of every PII-typed answer that is still plaintext (non-PII answers are left alone).
    private static void EncryptAnswers(SqliteConnection connection, SqliteTransaction transaction, IDataProtector protector)
    {
        var pending = new List<(long Id, string Value)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT a.id, a.value, f.field_type FROM answers a JOIN fields f ON f.id = a.field_id;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (!FieldTypeInfo.IsPersonalInformation(FieldTypeInfo.ParseStored(reader.GetString(2))))
                    continue;
                var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (value.Length == 0 || protector.IsEncoded(value))
                    continue;
                pending.Add((reader.GetInt64(0), value));
            }
        }
        UpdateValues(connection, transaction, protector, "UPDATE answers SET value = $v WHERE id = $id;", pending);
    }

    // Encrypts the OCR values JSON of every staged row that is still plaintext (the JSON often holds PII).
    private static void EncryptStaging(SqliteConnection connection, SqliteTransaction transaction, IDataProtector protector)
    {
        var pending = new List<(long Id, string Value)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, values_json FROM image_import_staging;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (value.Length == 0 || protector.IsEncoded(value))
                    continue;
                pending.Add((reader.GetInt64(0), value));
            }
        }
        UpdateValues(connection, transaction, protector, "UPDATE image_import_staging SET values_json = $v WHERE id = $id;", pending);
    }

    // Re-writes each (id, plaintext) pair encrypted, using the given UPDATE statement.
    private static void UpdateValues(SqliteConnection connection, SqliteTransaction transaction, IDataProtector protector, string updateSql, List<(long Id, string Value)> pending)
    {
        foreach (var (id, value) in pending)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = updateSql;
            update.Parameters.AddWithValue("$v", protector.Encode(value));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteSetting(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v);";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }
}
