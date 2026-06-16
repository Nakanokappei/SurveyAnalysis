using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Reads and writes projects — the project record plus its field definitions (データ項目) — to
// SQLite. Fields are written and read in their on-screen order via an explicit ordinal column.
public sealed class ProjectRepository
{
    private readonly AppDatabase _db;

    public ProjectRepository(AppDatabase db) => _db = db;

    // Saves a brand-new project with its fields in one transaction, then stamps the assigned id back
    // onto the project and returns it.
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

        transaction.Commit();
        project.Id = projectId;
        return projectId;
    }

    // Applies a schema edit: updates the project name and reconciles its field definitions by id.
    // Existing fields are updated in place (keeping their fields.id), fields the user removed in the
    // dialog are deleted (their answers cascade away), and newly added fields are inserted. Because
    // ids are preserved, imported answers — which reference fields.id — survive a rename / retype /
    // reorder of a field. All in one transaction.
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

        // Delete fields no longer in the draft first (before inserting new ones, whose fresh ids must
        // not be swept away). A field carries id 0 until saved, so the kept set is the positive ids.
        var keptIds = project.Fields.Where(f => f.Id > 0).Select(f => f.Id).ToHashSet();
        DeleteFieldsNotIn(connection, transaction, project.Id, keptIds);

        // Upsert each draft field at its on-screen ordinal: update the existing row, or insert a new one.
        var ordinal = 0;
        foreach (var field in project.Fields)
        {
            if (field.Id > 0)
                UpdateField(connection, transaction, project.Id, ordinal, field);
            else
                InsertField(connection, transaction, project.Id, ordinal, field);
            ordinal++;
        }

        transaction.Commit();
    }

    // Inserts one field row at the given ordinal within an open transaction, then stamps the assigned
    // id back onto the field so callers (and the new answers that reference it) can use it.
    private static void InsertField(SqliteConnection connection, SqliteTransaction transaction, long projectId, int ordinal, DataField field)
    {
        using var insertField = connection.CreateCommand();
        insertField.Transaction = transaction;
        insertField.CommandText = """
            INSERT INTO fields
                (project_id, ordinal, name, field_type, analysis,
                 use_for_aggregation, use_load_date_as_default)
            VALUES
                ($pid, $ord, $name, $type, $analysis, $agg, $loadDate);
            SELECT last_insert_rowid();
            """;
        insertField.Parameters.AddWithValue("$pid", projectId);
        insertField.Parameters.AddWithValue("$ord", ordinal);
        insertField.Parameters.AddWithValue("$name", field.Name);
        insertField.Parameters.AddWithValue("$type", field.FieldType.ToString());
        insertField.Parameters.AddWithValue("$analysis", field.Analysis.ToString());
        insertField.Parameters.AddWithValue("$agg", field.UseForAggregation ? 1 : 0);
        insertField.Parameters.AddWithValue("$loadDate", field.UseLoadDateAsDefault ? 1 : 0);
        field.Id = (long)insertField.ExecuteScalar()!;
    }

    // Updates an existing field row in place (matched by id, scoped to the project), at its ordinal.
    private static void UpdateField(SqliteConnection connection, SqliteTransaction transaction, long projectId, int ordinal, DataField field)
    {
        using var updateField = connection.CreateCommand();
        updateField.Transaction = transaction;
        updateField.CommandText = """
            UPDATE fields
            SET ordinal = $ord, name = $name, field_type = $type, analysis = $analysis,
                use_for_aggregation = $agg, use_load_date_as_default = $loadDate
            WHERE id = $id AND project_id = $pid;
            """;
        updateField.Parameters.AddWithValue("$ord", ordinal);
        updateField.Parameters.AddWithValue("$name", field.Name);
        updateField.Parameters.AddWithValue("$type", field.FieldType.ToString());
        updateField.Parameters.AddWithValue("$analysis", field.Analysis.ToString());
        updateField.Parameters.AddWithValue("$agg", field.UseForAggregation ? 1 : 0);
        updateField.Parameters.AddWithValue("$loadDate", field.UseLoadDateAsDefault ? 1 : 0);
        updateField.Parameters.AddWithValue("$id", field.Id);
        updateField.Parameters.AddWithValue("$pid", projectId);
        updateField.ExecuteNonQuery();
    }

    // Deletes the project's field rows whose id is not in the kept set (the fields removed in the
    // dialog). An empty kept set means every existing field was removed. Answers cascade away.
    private static void DeleteFieldsNotIn(SqliteConnection connection, SqliteTransaction transaction, long projectId, IReadOnlyCollection<long> keptIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$pid", projectId);
        if (keptIds.Count == 0)
        {
            command.CommandText = "DELETE FROM fields WHERE project_id = $pid;";
        }
        else
        {
            var placeholders = new List<string>();
            var index = 0;
            foreach (var id in keptIds)
            {
                var name = "$k" + index++;
                placeholders.Add(name);
                command.Parameters.AddWithValue(name, id);
            }
            command.CommandText = $"DELETE FROM fields WHERE project_id = $pid AND id NOT IN ({string.Join(",", placeholders)});";
        }
        command.ExecuteNonQuery();
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

    // Loads the full project — its fields in their saved order — or null if the id is gone.
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
                SELECT id, name, field_type, analysis, use_for_aggregation, use_load_date_as_default
                FROM fields WHERE project_id = $id ORDER BY ordinal;
                """;
            loadFields.Parameters.AddWithValue("$id", id);
            using var reader = loadFields.ExecuteReader();
            while (reader.Read())
            {
                project.Fields.Add(new DataField
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    FieldType = FieldTypeInfo.ParseStored(reader.GetString(2)),
                    Analysis = Enum.Parse<AnalysisMethod>(reader.GetString(3)),
                    UseForAggregation = reader.GetInt32(4) != 0,
                    UseLoadDateAsDefault = reader.GetInt32(5) != 0,
                });
            }
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
