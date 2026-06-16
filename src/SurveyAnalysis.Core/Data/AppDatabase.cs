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

    // The on-disk path of this database file (used by backup / optimize / restore maintenance).
    public string DatabaseFilePath { get; }

    public AppDatabase(string databaseFilePath)
    {
        DatabaseFilePath = databaseFilePath;
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

    // Brings the raw tables (source of truth) up to date through the migration runner, then (re)creates
    // the derived analytics star. The star is a rebuildable cache — AnalyticsRepository.Rebuild
    // repopulates it on demand — so it is dropped and recreated here, keeping its schema in step with
    // the code without a migration step. The raw tables evolve through versioned migrations instead.
    public void EnsureSchema()
    {
        using var connection = Open();

        // Schema setup is pure DDL that drops and recreates tables. Foreign-key enforcement must be off
        // during it: a raw migration that drops a table still referenced by the about-to-be-dropped
        // derived star would otherwise hit a dangling reference mid-rebuild. The pragma is a no-op
        // inside a transaction, so it is toggled here (outside any) and restored once the schema is
        // whole again. Runtime connections from Open() always have it on.
        SetForeignKeys(connection, false);
        MigrateRaw(connection);
        using (var derived = connection.CreateCommand())
        {
            derived.CommandText = DerivedSchemaSql;
            derived.ExecuteNonQuery();
        }
        SetForeignKeys(connection, true);
    }

    private static void SetForeignKeys(SqliteConnection connection, bool on)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = {(on ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }

    // The raw schema version this build expects. Bump it and add a matching case to MigrationSql when
    // the raw tables change; the runner then carries existing databases forward one step at a time.
    private const long CurrentRawVersion = 3;

    // Migrates the raw tables to CurrentRawVersion, using PRAGMA user_version (stored in the database
    // header) as the on-disk marker. Each step runs in its own transaction and advances the version,
    // so an interrupted upgrade resumes cleanly. The derived star is intentionally not versioned.
    private static void MigrateRaw(SqliteConnection connection)
    {
        var version = ReadUserVersion(connection);
        while (version < CurrentRawVersion)
        {
            var next = version + 1;
            using var transaction = connection.BeginTransaction();
            using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                // Advancing user_version inside the same transaction makes the step atomic.
                migrate.CommandText = MigrationSql(next) + $"\nPRAGMA user_version = {next};";
                migrate.ExecuteNonQuery();
            }
            transaction.Commit();
            version = next;
        }
    }

    private static long ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // The SQL for one migration step. Column names mirror the UI / domain wording (field_type=データ型,
    // analysis=分析方法, use_for_aggregation=集計の基準日) so the schema is greppable from the screens.
    private static string MigrationSql(long version) => version switch
    {
        1 => V1BaselineSql,
        2 => V2SourcePathSql,
        3 => V3UniqueNamesSql,
        _ => throw new InvalidOperationException($"No migration defined for raw schema version {version}."),
    };

    // v0 → v1: establish the versioned raw schema. Pre-versioning prototype databases keyed answers by
    // field_name and carried alert / months artefacts; that data is disposable (pre-production), so the
    // project tables are reset here while settings (window bounds, mail/LLM config) are preserved. A
    // brand-new database hits the same path with the DROPs as no-ops. Answers now reference the field
    // by id (rename-safe) and the alert columns are gone. Future steps must ALTER rather than reset.
    private const string V1BaselineSql = """
        DROP TABLE IF EXISTS answers;
        DROP TABLE IF EXISTS responses;
        DROP TABLE IF EXISTS months;
        DROP TABLE IF EXISTS fields;
        DROP TABLE IF EXISTS projects;

        CREATE TABLE projects (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT NOT NULL,
            created_utc  TEXT NOT NULL,
            updated_utc  TEXT NOT NULL
        );

        CREATE TABLE fields (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id                INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            ordinal                   INTEGER NOT NULL,
            name                      TEXT NOT NULL,
            field_type                TEXT NOT NULL,
            analysis                  TEXT NOT NULL,
            use_for_aggregation       INTEGER NOT NULL,
            use_load_date_as_default  INTEGER NOT NULL
        );

        CREATE TABLE responses (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id    INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            source        TEXT NOT NULL,
            imported_utc  TEXT NOT NULL
        );

        -- Answers reference their field by id (not name): a schema edit can rename a field without
        -- breaking the link, and removing a field cascade-deletes its answers.
        CREATE TABLE answers (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            response_id  INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE,
            field_id     INTEGER NOT NULL REFERENCES fields(id) ON DELETE CASCADE,
            value        TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key    TEXT PRIMARY KEY,
            value  TEXT NOT NULL
        );

        CREATE INDEX idx_fields_project    ON fields(project_id);
        CREATE INDEX idx_responses_project ON responses(project_id);
        CREATE INDEX idx_answers_response  ON answers(response_id);
        CREATE INDEX idx_answers_field     ON answers(field_id);
        """;

    // v1 → v2: record where a project's data came from (the imported CSV/TSV file path) so the welcome
    // list can show the source file and distinguish projects created from same-named files in different
    // folders. Existing projects keep NULL — their source path was never captured — and fall back to the
    // project name in the list. An ALTER (not a reset) so saved projects are preserved.
    private const string V2SourcePathSql = """
        ALTER TABLE projects ADD COLUMN source_path TEXT;
        """;

    // v2 → v3: project names are now unique and identify a project on their own, so the source file path
    // (added in v2 to tell same-named projects apart) is no longer needed — one database holds every
    // project. Drop the column, disambiguate any existing duplicate names by appending the row id (unique
    // and order-independent: the earliest of each name keeps it; every later duplicate still matches that
    // earliest row, so it is always renamed to a distinct value), then enforce uniqueness with an index.
    private const string V3UniqueNamesSql = """
        ALTER TABLE projects DROP COLUMN source_path;

        UPDATE projects
        SET name = name || ' (' || id || ')'
        WHERE EXISTS (
            SELECT 1 FROM projects earlier
            WHERE earlier.name = projects.name AND earlier.id < projects.id
        );

        CREATE UNIQUE INDEX idx_projects_name ON projects(name);
        """;

    // Analytics star schema, derived from responses/answers by ETL (AnalyticsRepository). One
    // fact_response row per response, with foreign keys to its dimensions and the sentiment measures;
    // a dimension the response lacks (no region field, topic before LLM) is NULL. Multi-valued choice
    // answers hang off the fact through the fact_response_choice bridge (a response can answer several
    // 選択肢 fields). dim_date is a full date dimension: one fact date_key yields every time grain
    // (fiscal year / quarter / month / ISO week / day-of-week) by grouping on the matching attribute;
    // dim_region carries the 都道府県 / 市区町村 split for hierarchical region queries. Fiscal year is
    // April-start (年度). The whole star is dropped + recreated on startup since it is rebuilt on demand.
    private const string DerivedSchemaSql = """
        DROP TABLE IF EXISTS fact_response_choice;
        DROP TABLE IF EXISTS fact_response;
        DROP TABLE IF EXISTS dim_choice;
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

        -- Region keyed by the full captured address (UNIQUE), with the parsed 都道府県 / 市区町村 split.
        CREATE TABLE dim_region (
            region_key  INTEGER PRIMARY KEY AUTOINCREMENT,
            label       TEXT NOT NULL UNIQUE,   -- the address as captured
            prefecture  TEXT NOT NULL,          -- 都道府県 (（不明） if unparseable)
            city        TEXT NOT NULL           -- 市区町村 ('' if absent)
        );

        CREATE TABLE dim_topic (
            topic_key  INTEGER PRIMARY KEY AUTOINCREMENT,
            label      TEXT NOT NULL UNIQUE
        );

        -- One distinct 選択肢 value per (field, value). field_id ties it to the project's field, so the
        -- same value under different choice fields stays distinct.
        CREATE TABLE dim_choice (
            choice_key  INTEGER PRIMARY KEY AUTOINCREMENT,
            field_id    INTEGER NOT NULL REFERENCES fields(id) ON DELETE CASCADE,
            value       TEXT NOT NULL,
            UNIQUE(field_id, value)
        );

        CREATE TABLE fact_response (
            fact_id          INTEGER PRIMARY KEY AUTOINCREMENT,
            response_id      INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE,
            project_id       INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            date_key         INTEGER REFERENCES dim_date(date_key),
            region_key       INTEGER REFERENCES dim_region(region_key),
            main_topic_key   INTEGER REFERENCES dim_topic(topic_key),   -- メイントピック (LLM 前は NULL)
            sentiment_score  REAL,                                       -- メイントピックの感情極性 (LLM 前は NULL)
            is_negative      INTEGER
        );

        -- Bridge: the 選択肢 answers of a response (a response can answer several choice fields).
        CREATE TABLE fact_response_choice (
            fact_id     INTEGER NOT NULL REFERENCES fact_response(fact_id) ON DELETE CASCADE,
            choice_key  INTEGER NOT NULL REFERENCES dim_choice(choice_key) ON DELETE CASCADE,
            PRIMARY KEY (fact_id, choice_key)
        );

        CREATE INDEX idx_fact_project ON fact_response(project_id);
        CREATE INDEX idx_fact_date    ON fact_response(date_key);
        CREATE INDEX idx_fact_region  ON fact_response(region_key);
        CREATE INDEX idx_frc_choice   ON fact_response_choice(choice_key);
        """;
}
