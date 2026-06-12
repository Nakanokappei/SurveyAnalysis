using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// Builds and queries the analytics star schema. Rebuild performs the ETL: it reads a project's raw
// responses/answers, resolves each response's date / region / topic dimension members, and writes
// one fact_response row per response. AggregateBy answers a slice query (group the facts by one
// dimension and count). Dimensions are conformed (shared across projects) and grown on demand.
public sealed class AnalyticsRepository
{
    private readonly AppDatabase _db;

    public AnalyticsRepository(AppDatabase db) => _db = db;

    // ===== Field-role mapping: which project field feeds each dimension =====

    // The aggregation date field drives the time dimension (and each fact's date).
    public static string? DateField(Project project) =>
        project.Fields.FirstOrDefault(f => f.UseForAggregation)?.Name;

    // An address / prefecture / city field drives the region dimension.
    public static string? RegionField(Project project) =>
        project.Fields.FirstOrDefault(f =>
            f.FieldType is FieldType.Address or FieldType.PrefectureOnly or FieldType.CityOnly)?.Name;

    // A topic-assignment field drives the topic dimension (populated once LLM analysis exists).
    public static string? TopicField(Project project) =>
        project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Topic)?.Name;

    // ===== ETL =====

    // Rebuilds a project's facts from its raw responses. Idempotent: the project's existing facts
    // are cleared first, so calling it again after an import simply refreshes the star.
    public void Rebuild(Project project)
    {
        var dateField = DateField(project);
        var regionField = RegionField(project);
        // Topic is left NULL until LLM topic assignment writes it; the raw field value is not a topic.

        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM fact_response WHERE project_id = $pid;";
            clear.Parameters.AddWithValue("$pid", project.Id);
            clear.ExecuteNonQuery();
        }

        foreach (var (responseId, values) in ReadResponses(connection, transaction, project.Id))
        {
            long? dateKey = null;
            if (dateField is not null && values.TryGetValue(dateField, out var dateValue) && TryParseDate(dateValue, out var date))
                dateKey = GetOrCreateDate(connection, transaction, date);

            long? regionKey = null;
            if (regionField is not null && values.TryGetValue(regionField, out var regionValue) && !string.IsNullOrWhiteSpace(regionValue))
                regionKey = GetOrCreateRegion(connection, transaction, regionValue.Trim());

            InsertFact(connection, transaction, responseId, project.Id, dateKey, regionKey);
        }

        transaction.Commit();
    }

    // ===== Slice query =====

    // Groups a project's facts by region or topic and returns (label, count), largest first. Facts
    // missing the dimension fall into a "（未設定）"/"（未分析）" bucket so the totals still add up.
    public IReadOnlyList<(string Label, int Count)> AggregateBy(long projectId, SliceKind kind)
    {
        var sql = kind switch
        {
            SliceKind.Region => """
                SELECT COALESCE(g.label, '（未設定）') AS label, COUNT(*) AS c
                FROM fact_response f
                LEFT JOIN dim_region g ON f.region_key = g.region_key
                WHERE f.project_id = $pid
                GROUP BY f.region_key
                ORDER BY c DESC;
                """,
            SliceKind.Topic => """
                SELECT COALESCE(t.label, '（未分析）') AS label, COUNT(*) AS c
                FROM fact_response f
                LEFT JOIN dim_topic t ON f.topic_key = t.topic_key
                WHERE f.project_id = $pid
                GROUP BY f.topic_key
                ORDER BY c DESC;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Use AggregateByTime for the time slice.")
        };
        return RunQuery(sql, projectId);
    }

    // Groups a project's facts by one time grain. Every grain reads the same fact date_key but
    // groups on a different dim_date attribute (fiscal year/quarter, calendar month, ISO week, or
    // day-of-week). Calendar grains are newest first; 曜日別 is Mon→Sun. Dateless facts bucket into
    // "（日付なし）" and sort last. The label/group expressions are fixed constants (no user input).
    public IReadOnlyList<(string Label, int Count)> AggregateByTime(long projectId, TimeGrain grain)
    {
        var (labelColumn, groupExpr, order) = grain switch
        {
            TimeGrain.FiscalYear => ("d.fiscal_year_label", "COALESCE(d.fiscal_year, -1)", "DESC"),
            TimeGrain.FiscalQuarter => ("d.fiscal_quarter_label", "COALESCE(d.fiscal_year * 10 + d.fiscal_quarter, -1)", "DESC"),
            TimeGrain.Month => ("d.month_label", "COALESCE(d.year * 100 + d.month, -1)", "DESC"),
            TimeGrain.Week => ("d.week_label", "COALESCE(d.week_year * 100 + d.week_of_year, -1)", "DESC"),
            TimeGrain.DayOfWeek => ("d.day_of_week_label", "COALESCE(d.day_of_week, 7)", "ASC"),
            _ => throw new ArgumentOutOfRangeException(nameof(grain))
        };

        var sql = $"""
            SELECT COALESCE({labelColumn}, '（日付なし）') AS label, COUNT(*) AS c
            FROM fact_response f
            LEFT JOIN dim_date d ON f.date_key = d.date_key
            WHERE f.project_id = $pid
            GROUP BY {groupExpr}
            ORDER BY {groupExpr} {order};
            """;
        return RunQuery(sql, projectId);
    }

    private IReadOnlyList<(string Label, int Count)> RunQuery(string sql, long projectId)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$pid", projectId);

        var result = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    // ===== Helpers =====

    // Reads a project's responses as (response id, field-name→value map).
    private static List<(long Id, Dictionary<string, string> Values)> ReadResponses(
        SqliteConnection connection, SqliteTransaction transaction, long projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.id, a.field_name, a.value
            FROM responses r
            LEFT JOIN answers a ON a.response_id = r.id
            WHERE r.project_id = $pid
            ORDER BY r.id;
            """;
        command.Parameters.AddWithValue("$pid", projectId);

        var byId = new Dictionary<long, Dictionary<string, string>>();
        var order = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!byId.TryGetValue(id, out var values))
            {
                values = new Dictionary<string, string>();
                byId[id] = values;
                order.Add(id);
            }
            if (!reader.IsDBNull(1))
                values[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }
        return order.Select(id => (id, byId[id])).ToList();
    }

    private static readonly string[] DayOfWeekLabels =
        { "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日" };

    // date_key is the yyyymmdd integer, so the dimension is upserted by its own key. Every time-slice
    // attribute (fiscal year/quarter, ISO week, day-of-week) is precomputed here so the slice query
    // is a plain GROUP BY. Fiscal year is April-start: Jan–Mar belong to the previous 年度.
    private static long GetOrCreateDate(SqliteConnection connection, SqliteTransaction transaction, DateTime date)
    {
        var key = date.Year * 10000 + date.Month * 100 + date.Day;

        var dayOfWeek = ((int)date.DayOfWeek + 6) % 7;            // .NET Sun=0..Sat=6 → Mon=0..Sun=6
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var weekOfYear = ISOWeek.GetWeekOfYear(date);
        var weekYear = ISOWeek.GetYear(date);
        var fiscalYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var fiscalQuarter = ((date.Month - 4 + 12) % 12) / 3 + 1; // Apr-Jun=1, Jul-Sep=2, Oct-Dec=3, Jan-Mar=4

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO dim_date
                (date_key, full_date, year, month, month_label,
                 week_year, week_of_year, week_label,
                 day, day_of_week, day_of_week_label, is_weekend,
                 fiscal_year, fiscal_quarter, fiscal_year_label, fiscal_quarter_label)
            VALUES
                ($key, $full, $y, $m, $monthLabel,
                 $wy, $woy, $weekLabel,
                 $d, $dow, $dowLabel, $weekend,
                 $fy, $fq, $fyLabel, $fqLabel);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$full", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$y", date.Year);
        command.Parameters.AddWithValue("$m", date.Month);
        command.Parameters.AddWithValue("$monthLabel", $"{date.Year}年{date.Month}月");
        command.Parameters.AddWithValue("$wy", weekYear);
        command.Parameters.AddWithValue("$woy", weekOfYear);
        command.Parameters.AddWithValue("$weekLabel", $"{weekYear}年 第{weekOfYear}週");
        command.Parameters.AddWithValue("$d", date.Day);
        command.Parameters.AddWithValue("$dow", dayOfWeek);
        command.Parameters.AddWithValue("$dowLabel", DayOfWeekLabels[dayOfWeek]);
        command.Parameters.AddWithValue("$weekend", isWeekend ? 1 : 0);
        command.Parameters.AddWithValue("$fy", fiscalYear);
        command.Parameters.AddWithValue("$fq", fiscalQuarter);
        command.Parameters.AddWithValue("$fyLabel", $"{fiscalYear}年度");
        command.Parameters.AddWithValue("$fqLabel", $"{fiscalYear}年度 Q{fiscalQuarter}");
        command.ExecuteNonQuery();
        return key;
    }

    // dim_region is keyed by a surrogate id, deduped by its (unique) label.
    private static long GetOrCreateRegion(SqliteConnection connection, SqliteTransaction transaction, string label)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO dim_region (label) VALUES ($label);
            SELECT region_key FROM dim_region WHERE label = $label;
            """;
        insert.Parameters.AddWithValue("$label", label);
        return (long)insert.ExecuteScalar()!;
    }

    private static void InsertFact(SqliteConnection connection, SqliteTransaction transaction,
        long responseId, long projectId, long? dateKey, long? regionKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fact_response (response_id, project_id, date_key, region_key, topic_key, sentiment_score, is_negative)
            VALUES ($rid, $pid, $date, $region, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$rid", responseId);
        command.Parameters.AddWithValue("$pid", projectId);
        command.Parameters.AddWithValue("$date", (object?)dateKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$region", (object?)regionKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static readonly string[] DateFormats =
        { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyy年M月d日" };

    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
