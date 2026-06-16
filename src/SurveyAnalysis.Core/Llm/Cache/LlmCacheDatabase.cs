using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SurveyAnalysis.Llm.Cache;

// Owns the LLM cache SQLite file — separate from the main app database so large embedding BLOBs do
// not bloat or lock the survey data, and so the cache never goes through the raw-migration framework.
// It is a pure disposable cache: the schema is additive (CREATE TABLE IF NOT EXISTS) and, being
// rebuildable, a future incompatible change may simply bump the version and drop+recreate. Mirrors
// AppDatabase in shape (Default / Open / EnsureSchema) but is intentionally simpler.
public sealed class LlmCacheDatabase
{
    private readonly string _connectionString;

    public LlmCacheDatabase(string databaseFilePath)
        => _connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFilePath }.ToString();

    // %LOCALAPPDATA%\SurveyAnalysis\llmcache.db (alongside surveyanalysis.db), machine-local.
    public static LlmCacheDatabase Default()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SurveyAnalysis");
        Directory.CreateDirectory(folder);
        return new LlmCacheDatabase(Path.Combine(folder, "llmcache.db"));
    }

    // WAL + a busy timeout so the parallel embedding/chat batches can write concurrently without
    // tripping "database is locked".
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql + "\nPRAGMA user_version = 1;";
        command.ExecuteNonQuery();
    }

    // Embedding vectors are stored as packed little-endian float32 BLOBs (dim*4 bytes), compact and
    // directly reusable by the future C# clustering. Chat responses store the assistant content plus
    // token usage. Both keyed by the HashKey-derived cache_key (provider identity is baked into it).
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS embedding_cache (
            cache_key    TEXT PRIMARY KEY,
            provider     TEXT NOT NULL,
            model        TEXT NOT NULL,
            dim          INTEGER NOT NULL,
            vector       BLOB NOT NULL,
            created_utc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chat_cache (
            cache_key          TEXT PRIMARY KEY,
            provider           TEXT NOT NULL,
            model              TEXT NOT NULL,
            content            TEXT NOT NULL,
            prompt_tokens      INTEGER,
            completion_tokens  INTEGER,
            created_utc        TEXT NOT NULL
        );
        """;
}
