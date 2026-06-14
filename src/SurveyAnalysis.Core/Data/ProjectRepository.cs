using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Reads and writes projects — the project record plus its field definitions (データ項目) and
// month labels — to SQLite. Fields and months are written and read in their on-screen order
// via an explicit ordinal column.
public sealed class ProjectRepository
{
    private readonly AppDatabase _db;

    public ProjectRepository(AppDatabase db) => _db = db;

    // Saves a brand-new project with its fields and months in one transaction, then stamps the
    // assigned id back onto the project and returns it.
    public long Insert(Project project)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        long projectId;
        using (var insertProject = connection.CreateCommand())
        {
            insertProject.Transaction = transaction;
            insertProject.CommandText = """
                INSERT INTO projects (name, created_utc, updated_utc) VALUES ($name, $now, $now);
                SELECT last_insert_rowid();
                """;
            insertProject.Parameters.AddWithValue("$name", project.Name);
            insertProject.Parameters.AddWithValue("$now", now);
            projectId = (long)insertProject.ExecuteScalar()!;
        }

        // Field rows, numbered by their position in the design screen.
        var ordinal = 0;
        foreach (var field in project.Fields)
            InsertField(connection, transaction, projectId, ordinal++, field);

        // Month labels, in sidebar order (newest first as supplied).
        ordinal = 0;
        foreach (var month in project.Months)
        {
            using var insertMonth = connection.CreateCommand();
            insertMonth.Transaction = transaction;
            insertMonth.CommandText = "INSERT INTO months (project_id, ordinal, label) VALUES ($pid, $ord, $label);";
            insertMonth.Parameters.AddWithValue("$pid", projectId);
            insertMonth.Parameters.AddWithValue("$ord", ordinal++);
            insertMonth.Parameters.AddWithValue("$label", month);
            insertMonth.ExecuteNonQuery();
        }

        transaction.Commit();
        project.Id = projectId;
        return projectId;
    }

    // Applies a schema edit: updates the project name and replaces its field definitions. The
    // design dialog does not edit month labels, so the months table is left untouched. Old field
    // rows are deleted and the current ones re-inserted in order, all in one transaction.
    // (Once survey responses reference fields, this will need a migration step; today nothing does.)
    public void Update(Project project)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        using (var updateProject = connection.CreateCommand())
        {
            updateProject.Transaction = transaction;
            updateProject.CommandText = "UPDATE projects SET name = $name, updated_utc = $now WHERE id = $id;";
            updateProject.Parameters.AddWithValue("$name", project.Name);
            updateProject.Parameters.AddWithValue("$now", now);
            updateProject.Parameters.AddWithValue("$id", project.Id);
            updateProject.ExecuteNonQuery();
        }

        // Replace the field set: clear the old rows, then re-insert the current ones in order.
        using (var deleteFields = connection.CreateCommand())
        {
            deleteFields.Transaction = transaction;
            deleteFields.CommandText = "DELETE FROM fields WHERE project_id = $id;";
            deleteFields.Parameters.AddWithValue("$id", project.Id);
            deleteFields.ExecuteNonQuery();
        }

        var ordinal = 0;
        foreach (var field in project.Fields)
            InsertField(connection, transaction, project.Id, ordinal++, field);

        transaction.Commit();
    }

    // Inserts one field row at the given ordinal within an open transaction. Shared by Insert and
    // Update so the column list and value mapping live in one place.
    private static void InsertField(SqliteConnection connection, SqliteTransaction transaction, long projectId, int ordinal, DataField field)
    {
        using var insertField = connection.CreateCommand();
        insertField.Transaction = transaction;
        insertField.CommandText = """
            INSERT INTO fields
                (project_id, ordinal, name, field_type, analysis,
                 use_for_aggregation, use_load_date_as_default, enable_alert, alert_threshold)
            VALUES
                ($pid, $ord, $name, $type, $analysis,
                 $agg, $loadDate, $alert, $threshold);
            """;
        insertField.Parameters.AddWithValue("$pid", projectId);
        insertField.Parameters.AddWithValue("$ord", ordinal);
        insertField.Parameters.AddWithValue("$name", field.Name);
        insertField.Parameters.AddWithValue("$type", field.FieldType.ToString());
        insertField.Parameters.AddWithValue("$analysis", field.Analysis.ToString());
        insertField.Parameters.AddWithValue("$agg", field.UseForAggregation ? 1 : 0);
        insertField.Parameters.AddWithValue("$loadDate", field.UseLoadDateAsDefault ? 1 : 0);
        // The alert columns are retained in the schema (NOT NULL) for backward compatibility but the
        // feature was removed; write neutral defaults.
        insertField.Parameters.AddWithValue("$alert", 1);
        insertField.Parameters.AddWithValue("$threshold", -0.5);
        insertField.ExecuteNonQuery();
    }

    // Lists saved projects for the welcome screen, newest-updated first, each with its field count.
    public IReadOnlyList<ProjectSummary> ListSummaries()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, p.updated_utc, COUNT(f.id) AS field_count
            FROM projects p
            LEFT JOIN fields f ON f.project_id = p.id
            GROUP BY p.id
            ORDER BY p.updated_utc DESC, p.id DESC;
            """;

        var summaries = new List<ProjectSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new ProjectSummary
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                UpdatedUtc = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                FieldCount = reader.GetInt32(3),
            });
        }
        return summaries;
    }

    // Loads the full project — fields and months in their saved order — or null if the id is gone.
    public Project? Load(long id)
    {
        using var connection = _db.Open();

        string name;
        using (var loadProject = connection.CreateCommand())
        {
            loadProject.CommandText = "SELECT name FROM projects WHERE id = $id;";
            loadProject.Parameters.AddWithValue("$id", id);
            if (loadProject.ExecuteScalar() is not string projectName)
                return null;
            name = projectName;
        }

        var project = new Project { Id = id, Name = name };

        using (var loadFields = connection.CreateCommand())
        {
            loadFields.CommandText = """
                SELECT name, field_type, analysis,
                       use_for_aggregation, use_load_date_as_default, enable_alert, alert_threshold
                FROM fields WHERE project_id = $id ORDER BY ordinal;
                """;
            loadFields.Parameters.AddWithValue("$id", id);
            using var reader = loadFields.ExecuteReader();
            while (reader.Read())
            {
                project.Fields.Add(new DataField
                {
                    Name = reader.GetString(0),
                    FieldType = FieldTypeInfo.ParseStored(reader.GetString(1)),
                    Analysis = Enum.Parse<AnalysisMethod>(reader.GetString(2)),
                    UseForAggregation = reader.GetInt32(3) != 0,
                    UseLoadDateAsDefault = reader.GetInt32(4) != 0,
                    // columns 5/6 (enable_alert, alert_threshold) are retained but unused.
                });
            }
        }

        using (var loadMonths = connection.CreateCommand())
        {
            loadMonths.CommandText = "SELECT label FROM months WHERE project_id = $id ORDER BY ordinal;";
            loadMonths.Parameters.AddWithValue("$id", id);
            using var reader = loadMonths.ExecuteReader();
            while (reader.Read())
                project.Months.Add(reader.GetString(0));
        }

        return project;
    }

    // Deletes a project; its fields and months go with it via ON DELETE CASCADE.
    public void Delete(long id)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
