using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SurveyAnalysis.Data;

// Owns the SQLite database: where it lives, how connections are opened, and the schema.
// Every repository takes an AppDatabase and opens a short-lived connection per operation.
// The file path is injectable so tests can point at a throwaway database.
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string databaseFilePath)
    {
        // ReadWriteCreate (the default) creates the file on first open.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFilePath }.ToString();
    }

    // The per-user database location: %LOCALAPPDATA%\SurveyAnalysis on Windows,
    // ~/Library/Application Support/SurveyAnalysis on macOS, ~/.local/share/SurveyAnalysis on
    // Linux. Machine-local (not roaming) because the survey data and settings belong to this
    // installation.
    public static AppDatabase Default()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SurveyAnalysis");
        Directory.CreateDirectory(folder);
        return new AppDatabase(Path.Combine(folder, "surveyanalysis.db"));
    }

    // Opens a connection with foreign-key enforcement on (SQLite leaves it off by default, so
    // ON DELETE CASCADE only works once this pragma is set for the connection).
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // Creates the tables if they do not yet exist. Safe to call on every startup.
    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    // Column names mirror the UI / domain wording (field_type=データ型, analysis=分析方法,
    // alert_threshold=アラート閾値, use_for_aggregation=月次集計) so the schema is greppable
    // from the screens. Enum-valued columns store the enum name as text, which stays readable
    // and survives enum reordering.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS projects (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT NOT NULL,
            created_utc  TEXT NOT NULL,
            updated_utc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fields (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id                INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            ordinal                   INTEGER NOT NULL,
            name                      TEXT NOT NULL,
            field_type                TEXT NOT NULL,
            analysis                  TEXT NOT NULL,
            use_for_aggregation       INTEGER NOT NULL,
            use_load_date_as_default  INTEGER NOT NULL,
            enable_alert              INTEGER NOT NULL,
            alert_threshold           REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS months (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            label       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS responses (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id    INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            source        TEXT NOT NULL,
            imported_utc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS answers (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            response_id  INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE,
            field_name   TEXT NOT NULL,
            value        TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key    TEXT PRIMARY KEY,
            value  TEXT NOT NULL
        );
        """;
}
