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
    public IReadOnlyList<(string Label, int Count)> AggregateBy(long projectId, SliceKind kind, long? fromKey = null, long? toKey = null)
    {
        // date_key lives on fact_response, so the 集計期間 window is a plain BETWEEN (no extra join);
        // it also drops dateless facts, which fall outside any window.
        var window = fromKey is not null && toKey is not null ? " AND f.date_key BETWEEN $from AND $to" : "";
        var sql = kind switch
        {
            SliceKind.Region => $"""
                SELECT COALESCE(g.label, '（未設定）') AS label, COUNT(*) AS c
                FROM fact_response f
                LEFT JOIN dim_region g ON f.region_key = g.region_key
                WHERE f.project_id = $pid{window}
                GROUP BY f.region_key
                ORDER BY c DESC;
                """,
            SliceKind.Topic => $"""
                SELECT COALESCE(t.label, '（未分析）') AS label, COUNT(*) AS c
                FROM fact_response f
                LEFT JOIN dim_topic t ON f.topic_key = t.topic_key
                WHERE f.project_id = $pid{window}
                GROUP BY f.topic_key
                ORDER BY c DESC;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Use AggregateByTime for the time slice.")
        };
        return RunQuery(sql, projectId, fromKey, toKey);
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

    private IReadOnlyList<(string Label, int Count)> RunQuery(string sql, long projectId, long? fromKey = null, long? toKey = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$pid", projectId);
        if (fromKey is { } fk && toKey is { } tk)
        {
            command.Parameters.AddWithValue("$from", fk);
            command.Parameters.AddWithValue("$to", tk);
        }

        var result = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    // ===== Time drill-down =====

    // The children of a scope, one level finer, largest first. Depth picks the grouping attribute:
    // 全期間→年度, 年度→月, 月→週, 週→日. The scope's accumulated predicates (carried down the path)
    // become the WHERE, so each child's count is a true subset and the children sum to the scope
    // total. Dateless facts have no dim_date row and so never appear in the time hierarchy (they show
    // in the dashboard / flat view instead). Group/label expressions are fixed constants — only the
    // scope's integer predicates are bound, never interpolated.
    public IReadOnlyList<TimeChild> DrillTimeChildren(long projectId, TimeScope scope, long? fromKey = null, long? toKey = null)
    {
        var (groupExpr, labelColumn) = scope.Depth switch
        {
            0 => ("d.fiscal_year", "d.fiscal_year_label"),
            1 => ("d.year * 100 + d.month", "d.month_label"),
            2 => ("d.week_year * 100 + d.week_of_year", "d.week_label"),
            3 => ("d.date_key", "d.full_date"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope.Depth, "The 日 level is terminal.")
        };

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        command.CommandText = $"""
            SELECT {labelColumn} AS label, COUNT(*) AS c,
                   d.fiscal_year, d.year, d.month, d.week_year, d.week_of_year, d.date_key
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            WHERE {where}
            GROUP BY {groupExpr}
            ORDER BY {groupExpr} DESC;
            """;

        var result = new List<TimeChild>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var label = reader.GetString(0);
            var count = reader.GetInt32(1);
            // Only the attribute(s) at the current grouping level are constant within a group; we read
            // just those to build the child scope (Depth + 1 with the new predicate added).
            var child = scope.Depth switch
            {
                0 => scope with { Depth = 1, FiscalYear = reader.GetInt64(2), Label = label },
                1 => scope with { Depth = 2, Year = reader.GetInt32(3), Month = reader.GetInt32(4), Label = label },
                2 => scope with { Depth = 3, WeekYear = reader.GetInt32(5), WeekOfYear = reader.GetInt32(6), Label = label },
                _ => scope with { Depth = 4, DateKey = reader.GetInt64(7), Label = label },
            };
            result.Add(new TimeChild(label, count, child));
        }
        return result;
    }

    // The day-of-week distribution within a scope and window (the 曜日 view), Mon→Sun.
    public IReadOnlyList<(string Label, int Count)> DayOfWeekForScope(long projectId, TimeScope scope, long? fromKey = null, long? toKey = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        command.CommandText = $"""
            SELECT d.day_of_week_label, COUNT(*) AS c
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            WHERE {where}
            GROUP BY d.day_of_week
            ORDER BY d.day_of_week ASC;
            """;

        var result = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    // The individual responses within a scope (the terminal 個票一覧), each as a field-name→value map
    // so the view model can build a display row (記入日 + 抜粋, PII hidden) the same way the dashboard
    // does. Filtered through the date dimension so only dated responses in the scope are returned.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ResponsesForScope(long projectId, TimeScope scope, long? fromKey = null, long? toKey = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        command.CommandText = $"""
            SELECT r.id, a.field_name, a.value
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            JOIN responses r ON r.id = f.response_id
            LEFT JOIN answers a ON a.response_id = r.id
            WHERE {where}
            ORDER BY r.id;
            """;
        return ReadResponseMaps(command);
    }

    // The individual responses on a given weekday within the window (the 曜日 view's terminal 個票一覧).
    // Weekday is not part of the drill scope, so it has its own predicate rather than going via TimeScope.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ResponsesForWeekday(long projectId, int dayOfWeek, long? fromKey = null, long? toKey = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        command.Parameters.AddWithValue("$dow", dayOfWeek);
        var window = "";
        if (fromKey is { } fk && toKey is { } tk)
        {
            window = " AND f.date_key BETWEEN $from AND $to";
            command.Parameters.AddWithValue("$from", fk);
            command.Parameters.AddWithValue("$to", tk);
        }
        command.CommandText = $"""
            SELECT r.id, a.field_name, a.value
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            JOIN responses r ON r.id = f.response_id
            LEFT JOIN answers a ON a.response_id = r.id
            WHERE f.project_id = $pid AND d.day_of_week = $dow{window}
            ORDER BY r.id;
            """;
        return ReadResponseMaps(command);
    }

    // Reads a response query (r.id, field_name, value) into one field-name→value map per response,
    // in row order. A response with no answers (LEFT JOIN null) keeps an empty map.
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadResponseMaps(SqliteCommand command)
    {
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
        return order.Select(id => (IReadOnlyDictionary<string, string>)byId[id]).ToList();
    }

    // ===== Analysis table (rows × all project fields) =====

    // Groups responses by the chosen dimension within the scope/window, and for every project field
    // produces one formatted cell using that field's aggregation (種類数 / 合計 / 平均 / 感情平均). For the
    // time dimension each row also carries the scope to drill into. Aggregation is done in memory over
    // the joined answer rows — the data is small and this keeps the per-field logic in one place.
    public IReadOnlyList<AnalysisRow> AggregateRows(
        long projectId, AnalysisGrouping grouping, TimeScope scope,
        long? fromKey, long? toKey, IReadOnlyList<AnalysisColumn> columns)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);

        // Time and 曜日 read from the date dimension; 地域/トピック join their own dimension. The answer
        // tail (rid / sentiment / field / value) is identical, so the in-memory aggregator is shared.
        command.CommandText = grouping switch
        {
            AnalysisGrouping.Time or AnalysisGrouping.Weekday => $"""
                SELECT d.fiscal_year AS fy, d.fiscal_year_label AS fylabel,
                       d.year AS yr, d.month AS mo, d.month_label AS molabel,
                       d.week_year AS wy, d.week_of_year AS wo, d.week_label AS wlabel,
                       d.date_key AS dkey, d.full_date AS fdate,
                       d.day_of_week AS dow, d.day_of_week_label AS dowlabel,
                       r.id AS rid, f.sentiment_score AS sscore, a.field_name AS fname, a.value AS val
                FROM fact_response f
                JOIN dim_date d ON f.date_key = d.date_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                WHERE {where};
                """,
            AnalysisGrouping.Region => $"""
                SELECT COALESCE(g.label, '（未設定）') AS glabel,
                       r.id AS rid, f.sentiment_score AS sscore, a.field_name AS fname, a.value AS val
                FROM fact_response f
                LEFT JOIN dim_region g ON f.region_key = g.region_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                WHERE {where};
                """,
            _ => $"""
                SELECT COALESCE(t.label, '（未分析）') AS glabel,
                       r.id AS rid, f.sentiment_score AS sscore, a.field_name AS fname, a.value AS val
                FROM fact_response f
                LEFT JOIN dim_topic t ON f.topic_key = t.topic_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                WHERE {where};
                """,
        };

        var groups = new Dictionary<string, Group>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var (key, label, child, sortKey) = Identify(reader, grouping, scope);
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new Group(label, child, sortKey);

                var rid = reader.GetInt64(reader.GetOrdinal("rid"));
                var sentiment = reader.IsDBNull(reader.GetOrdinal("sscore")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("sscore"));
                group.Sentiment[rid] = sentiment;

                var fnameOrdinal = reader.GetOrdinal("fname");
                if (!reader.IsDBNull(fnameOrdinal))
                {
                    var field = reader.GetString(fnameOrdinal);
                    var value = reader.IsDBNull(reader.GetOrdinal("val")) ? "" : reader.GetString(reader.GetOrdinal("val"));
                    group.AddAnswer(field, value);
                }
            }
        }

        // Largest-first for 地域/トピック; the time/weekday sort keys order chronologically / Mon→Sun.
        IEnumerable<Group> ordered = grouping switch
        {
            AnalysisGrouping.Region or AnalysisGrouping.Topic => groups.Values.OrderByDescending(g => g.Sentiment.Count),
            AnalysisGrouping.Weekday => groups.Values.OrderBy(g => g.SortKey),
            _ => groups.Values.OrderByDescending(g => g.SortKey),
        };

        return ordered
            .Select(g => new AnalysisRow(g.Label, columns.Select(c => g.Cell(c)).ToList(), g.Sentiment.Count, g.Child))
            .ToList();
    }

    // Extracts a group's identity (dedupe key, display label, drill scope, sort key) from a raw row.
    private static (string Key, string Label, TimeScope? Child, long SortKey) Identify(
        SqliteDataReader reader, AnalysisGrouping grouping, TimeScope scope)
    {
        if (grouping == AnalysisGrouping.Weekday)
        {
            var dow = reader.GetInt32(reader.GetOrdinal("dow"));
            return (dow.ToString(), reader.GetString(reader.GetOrdinal("dowlabel")), null, dow);
        }
        if (grouping is AnalysisGrouping.Region or AnalysisGrouping.Topic)
        {
            var label = reader.GetString(reader.GetOrdinal("glabel"));
            return (label, label, null, 0);
        }

        // Time: the grouping attribute depends on how deep the current scope is.
        return scope.Depth switch
        {
            0 => Year(reader, scope),
            1 => Month(reader, scope),
            2 => Week(reader, scope),
            _ => Day(reader, scope),
        };

        static (string, string, TimeScope?, long) Year(SqliteDataReader r, TimeScope s)
        {
            var fy = r.GetInt64(r.GetOrdinal("fy"));
            return (fy.ToString(), r.GetString(r.GetOrdinal("fylabel")), s with { Depth = 1, FiscalYear = fy, Label = r.GetString(r.GetOrdinal("fylabel")) }, fy);
        }
        static (string, string, TimeScope?, long) Month(SqliteDataReader r, TimeScope s)
        {
            int y = r.GetInt32(r.GetOrdinal("yr")), m = r.GetInt32(r.GetOrdinal("mo"));
            var label = r.GetString(r.GetOrdinal("molabel"));
            return ($"{y}-{m}", label, s with { Depth = 2, Year = y, Month = m, Label = label }, y * 100L + m);
        }
        static (string, string, TimeScope?, long) Week(SqliteDataReader r, TimeScope s)
        {
            int wy = r.GetInt32(r.GetOrdinal("wy")), wo = r.GetInt32(r.GetOrdinal("wo"));
            var label = r.GetString(r.GetOrdinal("wlabel"));
            return ($"{wy}-{wo}", label, s with { Depth = 3, WeekYear = wy, WeekOfYear = wo, Label = label }, wy * 100L + wo);
        }
        static (string, string, TimeScope?, long) Day(SqliteDataReader r, TimeScope s)
        {
            var key = r.GetInt64(r.GetOrdinal("dkey"));
            var label = r.GetString(r.GetOrdinal("fdate"));
            return (key.ToString(), label, s with { Depth = 4, DateKey = key, Label = label }, key);
        }
    }

    // In-memory accumulator for one dimension group: distinct values and numeric sums per field, plus
    // each response's sentiment score (deduped by response id).
    private sealed class Group
    {
        public string Label { get; }
        public TimeScope? Child { get; }
        public long SortKey { get; }
        public Dictionary<long, double?> Sentiment { get; } = new();
        private readonly Dictionary<string, HashSet<string>> _distinct = new();
        private readonly Dictionary<string, (double Sum, int N)> _numeric = new();

        public Group(string label, TimeScope? child, long sortKey)
        {
            Label = label;
            Child = child;
            SortKey = sortKey;
        }

        public void AddAnswer(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var trimmed = value.Trim();
            if (!_distinct.TryGetValue(field, out var set))
                _distinct[field] = set = new HashSet<string>();
            set.Add(trimmed);
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            {
                var cur = _numeric.GetValueOrDefault(field);
                _numeric[field] = (cur.Sum + num, cur.N + 1);
            }
        }

        // Formats one field's cell per its aggregation; non-numeric / empty data reads as "—".
        public string Cell(AnalysisColumn column) => column.Aggregation switch
        {
            FieldAggregation.DistinctCount => (_distinct.TryGetValue(column.Name, out var s) ? s.Count : 0).ToString(),
            FieldAggregation.Sum => _numeric.TryGetValue(column.Name, out var ns) && ns.N > 0 ? Number(ns.Sum) : "—",
            FieldAggregation.Average => _numeric.TryGetValue(column.Name, out var na) && na.N > 0 ? (na.Sum / na.N).ToString("0.0") : "—",
            _ => SentimentCell(),
        };

        private string SentimentCell()
        {
            var scores = Sentiment.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return scores.Count == 0 ? "—" : scores.Average().ToString("+0.00;-0.00;0.00");
        }

        private static string Number(double value) =>
            value == Math.Round(value) ? value.ToString("0") : value.ToString("0.0");
    }

    // Builds the WHERE for a scope: the project, whichever drill predicates are set, and (when given)
    // the 集計期間 date window. Returns the clause text; the matching integer parameters are bound onto
    // the command. Nothing is interpolated. date_key lives on fact_response, so the window needs no
    // extra join (and skips dateless facts, which fall outside any window).
    private static string ScopeWhere(TimeScope scope, long? fromKey, long? toKey, SqliteCommand command)
    {
        var clauses = new List<string> { "f.project_id = $pid" };
        if (scope.FiscalYear is { } fy) { clauses.Add("d.fiscal_year = $fy"); command.Parameters.AddWithValue("$fy", fy); }
        if (scope.Year is { } yr) { clauses.Add("d.year = $yr"); command.Parameters.AddWithValue("$yr", yr); }
        if (scope.Month is { } mo) { clauses.Add("d.month = $mo"); command.Parameters.AddWithValue("$mo", mo); }
        if (scope.WeekYear is { } wy) { clauses.Add("d.week_year = $wy"); command.Parameters.AddWithValue("$wy", wy); }
        if (scope.WeekOfYear is { } wo) { clauses.Add("d.week_of_year = $wo"); command.Parameters.AddWithValue("$wo", wo); }
        if (scope.DateKey is { } dk) { clauses.Add("f.date_key = $dk"); command.Parameters.AddWithValue("$dk", dk); }
        if (fromKey is { } fk && toKey is { } tk)
        {
            clauses.Add("f.date_key BETWEEN $from AND $to");
            command.Parameters.AddWithValue("$from", fk);
            command.Parameters.AddWithValue("$to", tk);
        }
        return string.Join(" AND ", clauses);
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
