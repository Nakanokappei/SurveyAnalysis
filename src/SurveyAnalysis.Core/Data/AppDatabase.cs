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

    // Creates the raw tables (source of truth) if missing, then (re)creates the derived analytics
    // star. The star tables are a rebuildable cache — AnalyticsRepository.Rebuild repopulates them
    // on demand — so they are dropped and recreated here, which keeps their schema in step with the
    // code without a migration step. The raw tables are never dropped.
    public void EnsureSchema()
    {
        using var connection = Open();
        using (var raw = connection.CreateCommand())
        {
            raw.CommandText = RawSchemaSql;
            raw.ExecuteNonQuery();
        }
        using (var derived = connection.CreateCommand())
        {
            derived.CommandText = DerivedSchemaSql;
            derived.ExecuteNonQuery();
        }
    }

    // Raw tables = the source of truth. Column names mirror the UI / domain wording (field_type=
    // データ型, analysis=分析方法, alert_threshold=アラート閾値, use_for_aggregation=月次集計) so the
    // schema is greppable from the screens. Enum-valued columns store the enum name as text.
    private const string RawSchemaSql = """
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

    // Analytics star schema, derived from responses/answers by ETL (AnalyticsRepository). One
    // fact_response row per response, with foreign keys to conformed dimensions and the sentiment
    // measures; a dimension the response lacks (no region field, topic before LLM) is NULL.
    // dim_date is a full date dimension: one fact date_key yields every time grain (fiscal year /
    // quarter / month / ISO week / day-of-week) by grouping on the matching attribute. Fiscal year
    // is April-start (年度). Dropped + recreated on startup since the data is rebuilt on demand.
    private const string DerivedSchemaSql = """
        DROP TABLE IF EXISTS fact_response;
        DROP TABLE IF EXISTS dim_date;
        DROP TABLE IF EXISTS dim_region;
        DROP TABLE IF EXISTS dim_topic;

        CREATE TABLE dim_date (
            date_key              INTEGER PRIMARY KEY,   -- yyyymmdd
            full_date             TEXT NOT NULL,          -- 2026-05-20
            year                  INTEGER NOT NULL,
            month                 INTEGER NOT NULL,
            month_label           TEXT NOT NULL,          -- 2026年5月
            week_year             INTEGER NOT NULL,       -- ISO week-numbering year
            week_of_year          INTEGER NOT NULL,       -- ISO week 1-53
            week_label            TEXT NOT NULL,          -- 2026年 第21週
            day                   INTEGER NOT NULL,
            day_of_week           INTEGER NOT NULL,       -- 0=月 .. 6=日 (sort order)
            day_of_week_label     TEXT NOT NULL,          -- 水曜日
            is_weekend            INTEGER NOT NULL,       -- 0/1
            fiscal_year           INTEGER NOT NULL,       -- April-start 年度
            fiscal_quarter        INTEGER NOT NULL,       -- 1-4 (Apr-Jun=1)
            fiscal_year_label     TEXT NOT NULL,          -- 2026年度
            fiscal_quarter_label  TEXT NOT NULL           -- 2026年度 Q1
        );

        CREATE TABLE dim_region (
            region_key  INTEGER PRIMARY KEY AUTOINCREMENT,
            label       TEXT NOT NULL UNIQUE
        );

        CREATE TABLE dim_topic (
            topic_key  INTEGER PRIMARY KEY AUTOINCREMENT,
            label      TEXT NOT NULL UNIQUE
        );

        CREATE TABLE fact_response (
            fact_id          INTEGER PRIMARY KEY AUTOINCREMENT,
            response_id      INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE,
            project_id       INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            date_key         INTEGER REFERENCES dim_date(date_key),
            region_key       INTEGER REFERENCES dim_region(region_key),
            topic_key        INTEGER REFERENCES dim_topic(topic_key),
            sentiment_score  REAL,
            is_negative      INTEGER
        );
        """;
}
