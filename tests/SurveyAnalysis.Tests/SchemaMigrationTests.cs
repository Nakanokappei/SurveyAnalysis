using System;
using System.IO;
using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

// The raw schema is versioned through PRAGMA user_version and brought forward by AppDatabase's
// migration runner. A fresh database lands at the current version, and re-running EnsureSchema is a
// no-op (the derived star is still recreated, but the raw migrations do not re-run).
public class SchemaMigrationTests
{
    private static long RawVersion(TempDatabase temp)
    {
        using var connection = temp.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void Fresh_database_is_at_the_current_raw_version()
    {
        using var temp = new TempDatabase();
        Assert.Equal(4L, RawVersion(temp));
        // v4 artifacts: projects.description and the per-column topic dictionary.
        Assert.Equal(1L, Scalar(temp.Db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='description';"));
        Assert.Equal(1L, Scalar(temp.Db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='field_topics';"));
    }

    [Fact]
    public void EnsureSchema_is_idempotent()
    {
        using var temp = new TempDatabase();
        temp.Db.EnsureSchema();
        temp.Db.EnsureSchema();
        Assert.Equal(4L, RawVersion(temp));
    }

    // A v1 database (projects without source_path, user_version 1) migrates forward to the current
    // version with its projects intact: v2 adds source_path, v3 drops it again and enforces unique names,
    // so the column is gone and the unique index is present at the end.
    [Fact]
    public void V1_database_migrates_to_current_keeping_projects()
    {
        var path = Path.Combine(Path.GetTempPath(), $"satest_v1_{Guid.NewGuid():N}.db");
        try
        {
            var db = new AppDatabase(path);

            // Build a minimal v1 raw schema (projects without source_path) and stamp user_version 1.
            using (var connection = db.Open())
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = """
                    CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    INSERT INTO projects (name, created_utc, updated_utc) VALUES ('既存プロジェクト', 'x', 'x');
                    PRAGMA user_version = 1;
                    """;
                setup.ExecuteNonQuery();
            }

            db.EnsureSchema();

            Assert.Equal(4L, Scalar(db, "PRAGMA user_version;"));
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='source_path';")); // dropped in v3
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_projects_name';"));
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='description';"));   // v4
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='field_topics';")); // v4
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM projects;"));                       // row kept
            Assert.Equal("既存プロジェクト", (string)ScalarObj(db, "SELECT name FROM projects;")); // name kept
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // v2 → v3 makes project names unique. A v2 database holding two identically-named projects is
    // de-duplicated (the earliest keeps the name, the later gains a suffix) and the unique index is
    // created, with both projects preserved and the (now unused) source_path column dropped.
    [Fact]
    public void V2_database_with_duplicate_names_is_deduplicated_on_migration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"satest_v2dup_{Guid.NewGuid():N}.db");
        try
        {
            var db = new AppDatabase(path);

            // Build a minimal v2 raw schema (projects with source_path) with two same-named projects.
            using (var connection = db.Open())
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = """
                    CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, source_path TEXT, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    INSERT INTO projects (name, created_utc, updated_utc) VALUES ('調査', 'x', 'x');
                    INSERT INTO projects (name, created_utc, updated_utc) VALUES ('調査', 'x', 'x');
                    PRAGMA user_version = 2;
                    """;
                setup.ExecuteNonQuery();
            }

            db.EnsureSchema();

            Assert.Equal(4L, Scalar(db, "PRAGMA user_version;"));
            Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM projects;"));                           // both kept
            Assert.Equal(2L, Scalar(db, "SELECT COUNT(DISTINCT name) FROM projects;"));               // names now distinct
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM projects WHERE name='調査';"));          // earliest keeps it
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_projects_name';"));
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='source_path';"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // A pre-versioning prototype database (answers keyed by field_name, alert columns, a months table,
    // user_version 0) must migrate all the way to the current version: the disposable project data is
    // reset (v1), settings are kept, the months table is gone, answers gains the field_id column, and the
    // projects.name unique index is in place (v3), with no leftover source_path column.
    [Fact]
    public void Legacy_v0_database_is_migrated_to_current_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"satest_legacy_{Guid.NewGuid():N}.db");
        try
        {
            var db = new AppDatabase(path);

            // Build the old raw schema with foreign keys (the drop order in the migration must respect
            // them) plus some data and a setting, and leave user_version at its default 0.
            using (var connection = db.Open())
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = """
                    CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    CREATE TABLE fields (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, ordinal INTEGER, name TEXT, field_type TEXT, analysis TEXT, use_for_aggregation INTEGER, use_load_date_as_default INTEGER, enable_alert INTEGER, alert_threshold REAL);
                    CREATE TABLE months (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, ordinal INTEGER, label TEXT);
                    CREATE TABLE responses (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, source TEXT, imported_utc TEXT);
                    CREATE TABLE answers (id INTEGER PRIMARY KEY AUTOINCREMENT, response_id INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE, field_name TEXT, value TEXT);
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    -- The old derived star: fact_response references responses/projects, which the raw
                    -- migration drops. This is what made a real legacy DB fail (dangling FK mid-rebuild).
                    CREATE TABLE dim_date (date_key INTEGER PRIMARY KEY);
                    CREATE TABLE dim_region (region_key INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL UNIQUE);
                    CREATE TABLE dim_topic (topic_key INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL UNIQUE);
                    CREATE TABLE fact_response (fact_id INTEGER PRIMARY KEY AUTOINCREMENT, response_id INTEGER NOT NULL REFERENCES responses(id) ON DELETE CASCADE, project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, date_key INTEGER REFERENCES dim_date(date_key), region_key INTEGER REFERENCES dim_region(region_key), topic_key INTEGER REFERENCES dim_topic(topic_key), sentiment_score REAL, is_negative INTEGER);
                    INSERT INTO projects (name, created_utc, updated_utc) VALUES ('旧プロジェクト', 'x', 'x');
                    INSERT INTO responses (project_id, source, imported_utc) VALUES (1, 's', 'x');
                    INSERT INTO fact_response (response_id, project_id) VALUES (1, 1);
                    INSERT INTO settings (key, value) VALUES ('window.bounds', '10,10,800,600');
                    """;
                setup.ExecuteNonQuery();
            }

            db.EnsureSchema();

            Assert.Equal(4L, Scalar(db, "PRAGMA user_version;"));
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM projects;"));                  // reset
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='months';")); // dropped
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('answers') WHERE name='field_id';"));
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='source_path';")); // added v2, dropped v3
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_projects_name';")); // v3
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name='description';"));   // v4
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='field_topics';")); // v4
            Assert.Equal("10,10,800,600", (string)ScalarObj(db, "SELECT value FROM settings WHERE key='window.bounds';")); // kept
        }
        finally
        {
            // Best effort: the connection pool may still hold the file handle, and a leftover temp
            // file is harmless (same reasoning as TempDatabase.Dispose).
            try { File.Delete(path); } catch { }
        }
    }

    private static long Scalar(AppDatabase db, string sql) => Convert.ToInt64(ScalarObj(db, sql));

    private static object ScalarObj(AppDatabase db, string sql)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
