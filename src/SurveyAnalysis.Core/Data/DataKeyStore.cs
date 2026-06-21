using System;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SurveyAnalysis.Data;

// Loads (or creates) a database's per-DB PII data key and returns a protector over it. The 32-byte AES key
// lives in the settings table, wrapped by SecretProtector (DPAPI / CurrentUser on Windows) — so the key is
// "the private key in the database file, encrypted by the master key (the OS user via DPAPI)". On first use
// the key is generated and stored; thereafter it is unwrapped into memory. If unwrapping fails (the database
// was moved to another user / machine, where DPAPI cannot open it), the protector is returned LOCKED, so PII
// reads are masked (🔒) rather than crashing.
public static class DataKeyStore
{
    private const string KeySetting = "pii_data_key";
    private const int KeyBytes = 32;   // AES-256

    public static IDataProtector Load(AppDatabase db)
    {
        using var connection = db.Open();
        var wrapped = ReadSetting(connection, KeySetting);

        byte[]? key;
        if (wrapped is null)
        {
            // First run for this database: mint a key and persist it DPAPI-wrapped.
            key = RandomNumberGenerator.GetBytes(KeyBytes);
            WriteSetting(connection, KeySetting, SecretProtector.Protect(Convert.ToBase64String(key)));
        }
        else
        {
            // SecretProtector.Unprotect returns "" when the DPAPI wrapper can't be opened here (foreign user).
            key = DecodeKey(SecretProtector.Unprotect(wrapped));
        }
        return new DpapiDataProtector(key);
    }

    // Parses a base64 key string into 32 bytes, or null when it is empty (unwrap failed) or malformed.
    private static byte[]? DecodeKey(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;
        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length == KeyBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v);";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }
}
