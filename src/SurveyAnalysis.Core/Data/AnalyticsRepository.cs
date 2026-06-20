using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Data;

// One response within a dashboard scope plus its persisted analysis: the answer values (for 記入日 /
// 抜粋 — PII is in the map but the view never shows it), the row sentiment score and negative flag, and
// the main topic label (null when unanalysed). The dashboard reads these straight from the star.
public sealed record ResponseAnalysis(
    IReadOnlyDictionary<string, string> Values,
    double? SentimentScore,
    bool IsNegative,
    string? Topic);

// One point of the 感情極性の推移 line: a month (year+month for ordering/labels), the average row
// sentiment over that month's analysed responses within the scope, and how many fed the average.
public sealed record SentimentTrendPoint(string AxisLabel, string Label, double Average, int Count);

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

    // An address field drives the region dimension.
    public static string? RegionField(Project project) =>
        project.Fields.FirstOrDefault(f => f.FieldType is FieldType.Address)?.Name;

    // A topic-assignment field drives the topic dimension (populated once LLM analysis exists).
    public static string? TopicField(Project project) =>
        project.Fields.FirstOrDefault(f => f.Analysis == AnalysisMethod.Topic)?.Name;

    // ===== ETL =====

    // Rebuilds a project's facts from its raw responses. Idempotent: the project's existing facts
    // are cleared first (the choice bridge cascades), so calling it again after an import simply
    // refreshes the star.
    public void Rebuild(Project project)
    {
        var dateField = DateField(project);
        var regionField = RegionField(project);
        // The 選択肢 fields each become a value in dim_choice, linked through the fact_response_choice
        // bridge. Resolved by name from the answer maps, keyed in the dimension by their stable id.
        var choiceFields = project.Fields
            .Where(f => f.FieldType == FieldType.Choice && f.Id > 0)
            .Select(f => (f.Id, f.Name))
            .ToList();
        // The primary 自由記述 field (first in design order) supplies fact_response.main_topic_key so the
        // existing トピック別 slice keeps working; richer per-column topics live in fact_response_topic.
        var primaryFreeTextFieldId = project.Fields
            .FirstOrDefault(f => f.FieldType == FieldType.FreeText && f.Id > 0)?.Id;

        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM fact_response WHERE project_id = $pid;";
            clear.Parameters.AddWithValue("$pid", project.Id);
            clear.ExecuteNonQuery();
        }

        // Project the persisted LLM analysis (raw → star): the topic dictionary into dim_topic, and the
        // per-response sentiment / topic assignments which the fact rows reference below.
        var topicKeyByTopicId = ProjectTopics(connection, transaction, project.Id);
        var sentimentByResponse = LoadResponseSentiment(connection, transaction, project.Id);
        var topicsByResponse = LoadResponseTopics(connection, transaction, project.Id);

        foreach (var (responseId, values) in ReadResponses(connection, transaction, project.Id))
        {
            long? dateKey = null;
            if (dateField is not null && values.TryGetValue(dateField, out var dateValue) && DateParsing.TryParse(dateValue, out var date))
                dateKey = GetOrCreateDate(connection, transaction, date);

            long? regionKey = null;
            if (regionField is not null && values.TryGetValue(regionField, out var regionValue) && !string.IsNullOrWhiteSpace(regionValue))
                regionKey = GetOrCreateRegion(connection, transaction, regionValue.Trim());

            var sentiment = sentimentByResponse.GetValueOrDefault(responseId);
            var assignments = topicsByResponse.GetValueOrDefault(responseId);

            // main_topic_key = the primary 自由記述 column's assigned topic for this response, if any.
            long? mainTopicKey = null;
            if (primaryFreeTextFieldId is { } pf && assignments is not null
                && assignments.TryGetValue(pf, out var primary) && primary.TopicId is { } primaryTopicId
                && topicKeyByTopicId.TryGetValue(primaryTopicId, out var pk))
                mainTopicKey = pk;

            var factId = InsertFact(connection, transaction, responseId, project.Id, dateKey, regionKey,
                sentiment.Score, sentiment.IsNegative, mainTopicKey);

            // Link each answered choice option to the fact through the bridge. A multi-select cell
            // ("A; B; C") is split into one bridge row per option, so each option is its own dimension
            // member (and a response can fall into several 選択肢別 buckets).
            foreach (var (fieldId, fieldName) in choiceFields)
                if (values.TryGetValue(fieldName, out var choiceValue))
                    foreach (var option in ChoiceValues.Split(choiceValue))
                    {
                        var choiceKey = GetOrCreateChoice(connection, transaction, fieldId, option);
                        InsertFactChoice(connection, transaction, factId, choiceKey);
                    }

            // Per 自由記述 column: its assigned topic + that column's sentiment, into the topic bridge.
            if (assignments is not null)
                foreach (var (fieldId, assignment) in assignments)
                {
                    long? topicKey = assignment.TopicId is { } tid && topicKeyByTopicId.TryGetValue(tid, out var tk) ? tk : null;
                    InsertFactTopic(connection, transaction, factId, fieldId, topicKey, assignment.Score, assignment.IsNegative);
                }
        }

        transaction.Commit();
    }

    // ===== Drill-down terminal: 個票一覧 =====

    // The individual responses within a scope (the terminal 個票一覧), each as a field-name→value map
    // so the view model can build a display row (記入日 + 抜粋, PII hidden) the same way the dashboard
    // does. Filtered through the date dimension so only dated responses in the scope are returned.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ResponsesForScope(long projectId, TimeScope scope, long? fromKey = null, long? toKey = null, bool newestFirst = false)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        var order = newestFirst ? "DESC" : "ASC";
        command.CommandText = $"""
            SELECT r.id, fl.name, a.value
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            JOIN responses r ON r.id = f.response_id
            LEFT JOIN answers a ON a.response_id = r.id
            LEFT JOIN fields fl ON fl.id = a.field_id
            WHERE {where}
            ORDER BY r.id {order};
            """;
        return ReadResponseMaps(command);
    }

    // Like ResponsesForScope but each response also carries its persisted analysis — the row sentiment
    // (fact_response.sentiment_score / is_negative) and its main topic label (via main_topic_key →
    // dim_topic). The dashboard uses this to show real KPIs, the topic / sentiment charts, and the per-
    // response 感情 / トピック columns from the same star the slices read. Always scoped to the root
    // (no drill) within the 集計期間 window.
    public IReadOnlyList<ResponseAnalysis> ResponsesWithAnalysisForScope(
        long projectId, long? fromKey, long? toKey, bool newestFirst = false)
        => ResponsesWithAnalysisForScope(projectId, TimeScope.Root, fromKey, toKey, newestFirst);

    // As above but for a specific drill scope, so the 期間 view's 日 terminal 個票 also carry each
    // response's 感情 / トピック.
    public IReadOnlyList<ResponseAnalysis> ResponsesWithAnalysisForScope(
        long projectId, TimeScope scope, long? fromKey, long? toKey, bool newestFirst = false)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        command.CommandText = AnalysisSelect
            + "JOIN dim_date d ON f.date_key = d.date_key "
            + "JOIN responses r ON r.id = f.response_id "
            + "LEFT JOIN dim_topic t ON f.main_topic_key = t.topic_key "
            + "LEFT JOIN answers a ON a.response_id = r.id "
            + "LEFT JOIN fields fl ON fl.id = a.field_id "
            + $"WHERE {where} ORDER BY r.id {(newestFirst ? "DESC" : "ASC")};";
        return ReadAnalysisMaps(command);
    }

    // The 個票 (with 感情 / トピック) for one weekday in the window — the 曜日 view's terminal.
    public IReadOnlyList<ResponseAnalysis> ResponsesWithAnalysisForWeekday(long projectId, int dayOfWeek, long? fromKey, long? toKey)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        command.Parameters.AddWithValue("$dow", dayOfWeek);
        command.CommandText = AnalysisSelect
            + "JOIN dim_date d ON f.date_key = d.date_key "
            + "JOIN responses r ON r.id = f.response_id "
            + "LEFT JOIN dim_topic t ON f.main_topic_key = t.topic_key "
            + "LEFT JOIN answers a ON a.response_id = r.id "
            + "LEFT JOIN fields fl ON fl.id = a.field_id "
            + $"WHERE f.project_id = $pid AND d.day_of_week = $dow{Window(fromKey, toKey, command)} ORDER BY r.id;";
        return ReadAnalysisMaps(command);
    }

    // The 個票 for one 都道府県 group in the window — the 地域別 view's terminal. The （未設定） group
    // (responses with no parsed region) is matched by a null region_key rather than a prefecture value.
    public IReadOnlyList<ResponseAnalysis> ResponsesWithAnalysisForRegion(long projectId, string prefecture, long? fromKey, long? toKey)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        string regionWhere;
        if (prefecture == UnsetRegion)
            regionWhere = "f.region_key IS NULL";
        else
        {
            regionWhere = "g.prefecture = $pref";
            command.Parameters.AddWithValue("$pref", prefecture);
        }
        command.CommandText = AnalysisSelect
            + "LEFT JOIN dim_region g ON f.region_key = g.region_key "
            + "JOIN responses r ON r.id = f.response_id "
            + "LEFT JOIN dim_topic t ON f.main_topic_key = t.topic_key "
            + "LEFT JOIN answers a ON a.response_id = r.id "
            + "LEFT JOIN fields fl ON fl.id = a.field_id "
            + $"WHERE f.project_id = $pid AND {regionWhere}{Window(fromKey, toKey, command)} ORDER BY r.id;";
        return ReadAnalysisMaps(command);
    }

    // The 個票 for one topic group in the window — the トピック別 view's terminal. Matches responses whose
    // 自由記述 was assigned this topic (fact_response_topic); （未分析） matches responses with no topic.
    public IReadOnlyList<ResponseAnalysis> ResponsesWithAnalysisForTopic(long projectId, string topicLabel, long? fromKey, long? toKey, long? topicFieldId = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        // When the topic report is scoped to one 質問 (自由記述 column), match only that column's topic.
        var fieldClause = "";
        if (topicFieldId is { } tf)
        {
            fieldClause = " AND z.field_id = $tf";
            command.Parameters.AddWithValue("$tf", tf);
        }
        string topicWhere;
        if (topicLabel == UnanalyzedTopic)
            topicWhere = $"NOT EXISTS (SELECT 1 FROM fact_response_topic z WHERE z.fact_id = f.fact_id AND z.topic_key IS NOT NULL{fieldClause})";
        else
        {
            topicWhere = $"EXISTS (SELECT 1 FROM fact_response_topic z JOIN dim_topic zt ON zt.topic_key = z.topic_key WHERE z.fact_id = f.fact_id AND zt.label = $label{fieldClause})";
            command.Parameters.AddWithValue("$label", topicLabel);
        }
        command.CommandText = AnalysisSelect
            + "JOIN responses r ON r.id = f.response_id "
            + "LEFT JOIN dim_topic t ON f.main_topic_key = t.topic_key "
            + "LEFT JOIN answers a ON a.response_id = r.id "
            + "LEFT JOIN fields fl ON fl.id = a.field_id "
            + $"WHERE f.project_id = $pid AND {topicWhere}{Window(fromKey, toKey, command)} ORDER BY r.id;";
        return ReadAnalysisMaps(command);
    }

    // The shared SELECT prefix (the column order ReadAnalysisMaps expects); callers append the joins and
    // their dimension's WHERE.
    private const string AnalysisSelect =
        "SELECT r.id, f.sentiment_score AS sscore, f.is_negative AS sneg, t.label AS topic, fl.name AS fname, a.value AS val "
        + "FROM fact_response f ";

    private const string UnsetRegion = "（未設定）";
    private const string UnanalyzedTopic = "（未分析）";

    // Appends the 集計期間 window predicate when both bounds are present, binding $from/$to onto the command.
    private static string Window(long? fromKey, long? toKey, SqliteCommand command)
    {
        if (fromKey is not { } fk || toKey is not { } tk)
            return "";
        command.Parameters.AddWithValue("$from", fk);
        command.Parameters.AddWithValue("$to", tk);
        return " AND f.date_key BETWEEN $from AND $to";
    }

    // Reads an analysis 個票 query (r.id, sscore, sneg, topic, fname, val) into one ResponseAnalysis per
    // response, in row order: the first row fixes its (constant) analysis, the rest accumulate answers.
    private static IReadOnlyList<ResponseAnalysis> ReadAnalysisMaps(SqliteCommand command)
    {
        var values = new Dictionary<long, Dictionary<string, string>>();
        var analysis = new Dictionary<long, (double? Score, bool IsNegative, string? Topic)>();
        var order = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!values.TryGetValue(id, out var map))
            {
                values[id] = map = new Dictionary<string, string>();
                analysis[id] = (
                    reader.IsDBNull(1) ? null : reader.GetDouble(1),
                    !reader.IsDBNull(2) && reader.GetInt32(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3));
                order.Add(id);
            }
            if (!reader.IsDBNull(4))
                map[reader.GetString(4)] = reader.IsDBNull(5) ? "" : reader.GetString(5);
        }
        return order
            .Select(id => new ResponseAnalysis(values[id], analysis[id].Score, analysis[id].IsNegative, analysis[id].Topic))
            .ToList();
    }

    // The average row sentiment over the 集計期間 window (chronological), bucketed by day when the dated
    // scored data spans ≤ 30 days and by ISO week otherwise — a short period reads day-by-day, a long one
    // stays legible. Buckets with no analysed response are omitted. Dateless facts are excluded by the
    // dim_date join. Drives the 感情極性の推移 line; the *For… overloads scope it to a drilled selection so
    // the line follows a drill-down / -up.
    public IReadOnlyList<SentimentTrendPoint> SentimentTrend(long projectId, long? fromKey, long? toKey)
        => SentimentTrend(projectId, fromKey, toKey, TrendFilter.ForScope(TimeScope.Root));

    // Scoped to a 時間別 drill scope (年度 / 月 / 週 / 日), so drilling zooms the trend into that period.
    public IReadOnlyList<SentimentTrendPoint> SentimentTrendForScope(long projectId, TimeScope scope, long? fromKey, long? toKey)
        => SentimentTrend(projectId, fromKey, toKey, TrendFilter.ForScope(scope));

    // Scoped to one 都道府県 (the 地域別 drill); （未設定） matches responses with no parsed region.
    public IReadOnlyList<SentimentTrendPoint> SentimentTrendForRegion(long projectId, string prefecture, long? fromKey, long? toKey)
        => SentimentTrend(projectId, fromKey, toKey, RegionTrendFilter(prefecture));

    // Scoped to one topic (the トピック別 drill); （未分析） matches responses with no topic. topicFieldId
    // limits it to one 質問's topic when the report is per-question.
    public IReadOnlyList<SentimentTrendPoint> SentimentTrendForTopic(long projectId, string topicLabel, long? fromKey, long? toKey, long? topicFieldId = null)
        => SentimentTrend(projectId, fromKey, toKey, TopicTrendFilter(topicLabel, topicFieldId));

    // Scoped to one weekday (the 曜日 drill): the trend over time of just that day-of-week.
    public IReadOnlyList<SentimentTrendPoint> SentimentTrendForWeekday(long projectId, int dayOfWeek, long? fromKey, long? toKey)
        => SentimentTrend(projectId, fromKey, toKey, WeekdayTrendFilter(dayOfWeek));

    // One query: the average row sentiment per day (with its ISO-week attributes) for the filtered facts.
    // If the span is ≤ 30 days the days are the points; otherwise they are merged into ISO weeks (a
    // count-weighted average), so a long period stays legible without a second round-trip to the database.
    private IReadOnlyList<SentimentTrendPoint> SentimentTrend(long projectId, long? fromKey, long? toKey, TrendFilter filter)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var (joins, extra) = filter.Build(command);
        var where = ScopeWhere(filter.Scope, fromKey, toKey, command) + extra;
        command.CommandText =
            "SELECT d.date_key AS k, d.week_year AS wy, d.week_of_year AS wo, d.week_label AS wlabel, "
            + "AVG(f.sentiment_score) AS avg, COUNT(f.sentiment_score) AS n "
            + $"FROM fact_response f JOIN dim_date d ON f.date_key = d.date_key {joins} "
            + $"WHERE {where} AND f.sentiment_score IS NOT NULL GROUP BY d.date_key ORDER BY d.date_key;";

        var days = new List<(long Key, int WeekYear, int WeekOfYear, string WeekLabel, double Avg, int Count)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                days.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetDouble(4), reader.GetInt32(5)));
        if (days.Count == 0)
            return Array.Empty<SentimentTrendPoint>();

        // ≤ 30-day span → one point per day.
        if ((KeyToDate(days[^1].Key) - KeyToDate(days[0].Key)).Days <= 30)
            return days.Select(d => TrendPoint(d.Key, KeyToDate(d.Key).ToString("yyyy/MM/dd"), d.Avg, d.Count)).ToList();

        // Otherwise merge the days into ISO weeks (count-weighted average), keyed by each week's first day.
        return days
            .GroupBy(d => (d.WeekYear, d.WeekOfYear))
            .OrderBy(g => g.Key.WeekYear).ThenBy(g => g.Key.WeekOfYear)
            .Select(g =>
            {
                var count = g.Sum(d => d.Count);
                var avg = count == 0 ? 0 : g.Sum(d => d.Avg * d.Count) / count;
                return TrendPoint(g.Min(d => d.Key), g.First().WeekLabel, avg, count);
            })
            .ToList();
    }

    // A trend point from a representative date_key: the short axis label is that date's M/d.
    private static SentimentTrendPoint TrendPoint(long key, string label, double average, int count)
    {
        var date = KeyToDate(key);
        return new SentimentTrendPoint($"{date.Month}/{date.Day}", label, average, count);
    }

    // A trend's fact-filter: the time scope, plus a builder that binds its dimension params on a command and
    // returns any extra JOIN(s) and an extra WHERE fragment (already prefixed with " AND ", or empty).
    private sealed record TrendFilter(TimeScope Scope, Func<SqliteCommand, (string Joins, string Where)> Build)
    {
        public static TrendFilter ForScope(TimeScope scope) => new(scope, _ => ("", ""));
    }

    private static TrendFilter RegionTrendFilter(string prefecture) => new(TimeScope.Root, command =>
    {
        const string join = "LEFT JOIN dim_region g ON f.region_key = g.region_key";
        if (prefecture == UnsetRegion)
            return (join, " AND f.region_key IS NULL");
        command.Parameters.AddWithValue("$pref", prefecture);
        return (join, " AND g.prefecture = $pref");
    });

    private static TrendFilter TopicTrendFilter(string topicLabel, long? topicFieldId) => new(TimeScope.Root, command =>
    {
        var fieldClause = "";
        if (topicFieldId is { } tf)
        {
            fieldClause = " AND z.field_id = $tf";
            command.Parameters.AddWithValue("$tf", tf);
        }
        if (topicLabel == UnanalyzedTopic)
            return ("", $" AND NOT EXISTS (SELECT 1 FROM fact_response_topic z WHERE z.fact_id = f.fact_id AND z.topic_key IS NOT NULL{fieldClause})");
        command.Parameters.AddWithValue("$label", topicLabel);
        return ("", $" AND EXISTS (SELECT 1 FROM fact_response_topic z JOIN dim_topic zt ON zt.topic_key = z.topic_key WHERE z.fact_id = f.fact_id AND zt.label = $label{fieldClause})");
    });

    private static TrendFilter WeekdayTrendFilter(int dayOfWeek) => new(TimeScope.Root, command =>
    {
        command.Parameters.AddWithValue("$dow", dayOfWeek);
        return ("", " AND d.day_of_week = $dow");
    });

    // A yyyymmdd date_key as a DateTime — for span maths and the short axis label.
    private static DateTime KeyToDate(long key) =>
        new((int)(key / 10000), (int)(key / 100 % 100), (int)(key % 100));

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
            SELECT r.id, fl.name, a.value
            FROM fact_response f
            JOIN dim_date d ON f.date_key = d.date_key
            JOIN responses r ON r.id = f.response_id
            LEFT JOIN answers a ON a.response_id = r.id
            LEFT JOIN fields fl ON fl.id = a.field_id
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
    // time dimension each row also carries the scope to drill into. Choice grouping needs the id of the
    // 選択肢 field to group by. Aggregation is done in memory over the joined answer rows — the data is
    // small and this keeps the per-field logic in one place.
    public AnalysisTable AggregateRows(
        long projectId, AnalysisGrouping grouping, TimeScope scope,
        long? fromKey, long? toKey, IReadOnlyList<AnalysisColumn> columns, long? choiceFieldId = null, long? topicFieldId = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pid", projectId);
        var where = ScopeWhere(scope, fromKey, toKey, command);
        if (grouping == AnalysisGrouping.Choice)
            command.Parameters.AddWithValue("$cf", choiceFieldId ?? 0);

        // トピック別 can be scoped to one 自由記述 column (a single 質問): the topic bridge join is then
        // narrowed to that field, so a multi-question project reports each question's own topics + sentiment.
        // Unscoped (null), it spans every 自由記述 column as before.
        var topicJoin = "";
        if (grouping == AnalysisGrouping.Topic && topicFieldId is { } tf)
        {
            topicJoin = " AND frt.field_id = $tf";
            command.Parameters.AddWithValue("$tf", tf);
        }

        // Time and 曜日 read from the date dimension; 地域/トピック/選択肢 join their own dimension. The
        // answer tail (rid / sentiment / field / value) is identical, so the in-memory aggregator is
        // shared. 地域 groups by 都道府県 (the dimension's parsed top level); 選択肢 pulls just the target
        // field's value from the choice bridge (one value per response) so other choice fields do not
        // split the response into a phantom （未選択） group.
        command.CommandText = grouping switch
        {
            AnalysisGrouping.Time or AnalysisGrouping.Weekday => $"""
                SELECT d.fiscal_year AS fy, d.fiscal_year_label AS fylabel,
                       d.year AS yr, d.month AS mo, d.month_label AS molabel,
                       d.week_year AS wy, d.week_of_year AS wo, d.week_label AS wlabel,
                       d.date_key AS dkey, d.full_date AS fdate,
                       d.day_of_week AS dow, d.day_of_week_label AS dowlabel,
                       r.id AS rid, f.sentiment_score AS sscore, fl.name AS fname, a.value AS val
                FROM fact_response f
                JOIN dim_date d ON f.date_key = d.date_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                LEFT JOIN fields fl ON fl.id = a.field_id
                WHERE {where};
                """,
            AnalysisGrouping.Region => $"""
                SELECT COALESCE(g.prefecture, '（未設定）') AS glabel,
                       r.id AS rid, f.sentiment_score AS sscore, fl.name AS fname, a.value AS val
                FROM fact_response f
                LEFT JOIN dim_region g ON f.region_key = g.region_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                LEFT JOIN fields fl ON fl.id = a.field_id
                WHERE {where};
                """,
            AnalysisGrouping.Choice => $"""
                SELECT COALESCE(c.value, '（未選択）') AS glabel,
                       r.id AS rid, f.sentiment_score AS sscore, fl.name AS fname, a.value AS val
                FROM fact_response f
                LEFT JOIN (
                    SELECT frc.fact_id, dc.value
                    FROM fact_response_choice frc
                    JOIN dim_choice dc ON dc.choice_key = frc.choice_key
                    WHERE dc.field_id = $cf
                ) c ON c.fact_id = f.fact_id
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                LEFT JOIN fields fl ON fl.id = a.field_id
                WHERE {where};
                """,
            // トピック別 groups by the per-自由記述-column topic assignment (fact_response_topic), and its
            // 感情極性 column is that topic's own sentiment (frt.sentiment_score) — not the row sentiment.
            // A response with no topic falls in （未分析）; with several 自由記述 columns it appears under
            // each column's topic.
            _ => $"""
                SELECT COALESCE(t.label, '（未分析）') AS glabel,
                       r.id AS rid, frt.sentiment_score AS sscore, fl.name AS fname, a.value AS val
                FROM fact_response f
                LEFT JOIN fact_response_topic frt ON frt.fact_id = f.fact_id{topicJoin}
                LEFT JOIN dim_topic t ON frt.topic_key = t.topic_key
                JOIN responses r ON r.id = f.response_id
                LEFT JOIN answers a ON a.response_id = r.id
                LEFT JOIN fields fl ON fl.id = a.field_id
                WHERE {where};
                """,
        };

        var groups = new Dictionary<string, Group>();
        var grand = new Group("全体", null, 0); // every response also folds into the total row
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
                grand.Sentiment[rid] = sentiment;

                var fnameOrdinal = reader.GetOrdinal("fname");
                if (!reader.IsDBNull(fnameOrdinal))
                {
                    var field = reader.GetString(fnameOrdinal);
                    var value = reader.IsDBNull(reader.GetOrdinal("val")) ? "" : reader.GetString(reader.GetOrdinal("val"));
                    group.AddAnswer(field, value);
                    grand.AddAnswer(field, value);
                }
            }
        }

        // Largest-first for 地域/トピック/選択肢; the time/weekday sort keys order chronologically / Mon→Sun.
        IEnumerable<Group> ordered = grouping switch
        {
            AnalysisGrouping.Region or AnalysisGrouping.Topic or AnalysisGrouping.Choice => groups.Values.OrderByDescending(g => g.Sentiment.Count),
            AnalysisGrouping.Weekday => groups.Values.OrderBy(g => g.SortKey),
            _ => groups.Values.OrderByDescending(g => g.SortKey),
        };

        var rows = ordered
            .Select(g => new AnalysisRow(g.Label, columns.Select(c => g.Cell(c)).ToList(), g.Sentiment.Count, g.Child, g.SentimentCell()))
            .ToList();

        // 全体 row: each column is aggregated over all responses with its own method — 種類数 counts the
        // distinct values across the whole set (not the response count), 合計 sums, 平均 averages — plus
        // the overall average 感情極性.
        var totalCount = grand.Sentiment.Count;
        var totalCells = columns.Select(c => grand.Cell(c)).ToList();
        return new AnalysisTable(rows, new AnalysisRow("全体", totalCells, totalCount, null, grand.SentimentCell()));
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
        if (grouping is AnalysisGrouping.Region or AnalysisGrouping.Topic or AnalysisGrouping.Choice)
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

        // The group's average 感情極性, shown as its own column in every report ("+0.00" / "—" when none
        // of the group's responses are scored). For the time/region/weekday/choice groupings this averages
        // the row sentiment (fact_response.sentiment_score); for トピック別 it averages the per-topic
        // sentiment (fact_response_topic.sentiment_score), since that query feeds that score in as sscore.
        public string SentimentCell()
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
            SELECT r.id, fl.name, a.value
            FROM responses r
            LEFT JOIN answers a ON a.response_id = r.id
            LEFT JOIN fields fl ON fl.id = a.field_id
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

    // dim_region is keyed by a surrogate id, deduped by its (unique) full-address label. The 都道府県 /
    // 市区町村 split is parsed once and stored so region queries can group by either level.
    private static long GetOrCreateRegion(SqliteConnection connection, SqliteTransaction transaction, string label)
    {
        var (prefecture, city) = AddressParser.Parse(label);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO dim_region (label, prefecture, city) VALUES ($label, $pref, $city);
            SELECT region_key FROM dim_region WHERE label = $label;
            """;
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$pref", prefecture);
        insert.Parameters.AddWithValue("$city", city);
        return (long)insert.ExecuteScalar()!;
    }

    // dim_choice is keyed by a surrogate id, deduped by (field_id, value) so the same text under two
    // different choice fields stays distinct.
    private static long GetOrCreateChoice(SqliteConnection connection, SqliteTransaction transaction, long fieldId, string value)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO dim_choice (field_id, value) VALUES ($fid, $value);
            SELECT choice_key FROM dim_choice WHERE field_id = $fid AND value = $value;
            """;
        insert.Parameters.AddWithValue("$fid", fieldId);
        insert.Parameters.AddWithValue("$value", value);
        return (long)insert.ExecuteScalar()!;
    }

    // Inserts one fact and returns its id (needed to link its choice / topic answers through the bridges).
    // Sentiment and main topic come from the persisted LLM analysis (NULL when a response is unanalysed).
    private static long InsertFact(SqliteConnection connection, SqliteTransaction transaction,
        long responseId, long projectId, long? dateKey, long? regionKey,
        double? sentimentScore, int? isNegative, long? mainTopicKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fact_response (response_id, project_id, date_key, region_key, main_topic_key, sentiment_score, is_negative)
            VALUES ($rid, $pid, $date, $region, $topic, $sent, $neg);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$rid", responseId);
        command.Parameters.AddWithValue("$pid", projectId);
        command.Parameters.AddWithValue("$date", (object?)dateKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$region", (object?)regionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$topic", (object?)mainTopicKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$sent", (object?)sentimentScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$neg", (object?)isNegative ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    // Links a fact to a 自由記述 column's assigned topic + that column's sentiment (one row per field).
    private static void InsertFactTopic(SqliteConnection connection, SqliteTransaction transaction,
        long factId, long fieldId, long? topicKey, double? sentimentScore, int? isNegative)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO fact_response_topic (fact_id, field_id, topic_key, sentiment_score, is_negative)
            VALUES ($fid, $field, $topic, $sent, $neg);
            """;
        command.Parameters.AddWithValue("$fid", factId);
        command.Parameters.AddWithValue("$field", fieldId);
        command.Parameters.AddWithValue("$topic", (object?)topicKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$sent", (object?)sentimentScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$neg", (object?)isNegative ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // Projects the field_topics dictionary into dim_topic (one row per (field, label)) and returns a map
    // from field_topics.id to the dim_topic.topic_key so the fact bridges can reference the star key.
    private static Dictionary<long, long> ProjectTopics(SqliteConnection connection, SqliteTransaction transaction, long projectId)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT ft.id, ft.field_id, ft.label
            FROM field_topics ft
            JOIN fields f ON f.id = ft.field_id
            WHERE f.project_id = $pid;
            """;
        read.Parameters.AddWithValue("$pid", projectId);

        var rows = new List<(long TopicId, long FieldId, string Label)>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));

        var map = new Dictionary<long, long>();
        foreach (var (topicId, fieldId, label) in rows)
            map[topicId] = GetOrCreateTopic(connection, transaction, fieldId, label);
        return map;
    }

    // dim_topic is keyed by a surrogate id, deduped by (field_id, label).
    private static long GetOrCreateTopic(SqliteConnection connection, SqliteTransaction transaction, long fieldId, string label)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO dim_topic (field_id, label) VALUES ($fid, $label);
            SELECT topic_key FROM dim_topic WHERE field_id = $fid AND label = $label;
            """;
        insert.Parameters.AddWithValue("$fid", fieldId);
        insert.Parameters.AddWithValue("$label", label);
        return (long)insert.ExecuteScalar()!;
    }

    // The per-response row sentiment (response_sentiment), for this project's responses.
    private static Dictionary<long, (double? Score, int? IsNegative)> LoadResponseSentiment(
        SqliteConnection connection, SqliteTransaction transaction, long projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rs.response_id, rs.score, rs.is_negative
            FROM response_sentiment rs
            JOIN responses r ON r.id = rs.response_id
            WHERE r.project_id = $pid;
            """;
        command.Parameters.AddWithValue("$pid", projectId);

        var map = new Dictionary<long, (double?, int?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            map[reader.GetInt64(0)] = (
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2));
        return map;
    }

    // The per-(response, 自由記述 field) topic assignment + sentiment (response_topic), grouped by response.
    private static Dictionary<long, Dictionary<long, (long? TopicId, double? Score, int? IsNegative)>> LoadResponseTopics(
        SqliteConnection connection, SqliteTransaction transaction, long projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rt.response_id, rt.field_id, rt.topic_id, rt.score, rt.is_negative
            FROM response_topic rt
            JOIN responses r ON r.id = rt.response_id
            WHERE r.project_id = $pid;
            """;
        command.Parameters.AddWithValue("$pid", projectId);

        var map = new Dictionary<long, Dictionary<long, (long?, double?, int?)>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var responseId = reader.GetInt64(0);
            if (!map.TryGetValue(responseId, out var byField))
                map[responseId] = byField = new Dictionary<long, (long?, double?, int?)>();
            byField[reader.GetInt64(1)] = (
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4));
        }
        return map;
    }

    // Links a choice value to a fact (idempotent on the (fact, choice) pair).
    private static void InsertFactChoice(SqliteConnection connection, SqliteTransaction transaction, long factId, long choiceKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO fact_response_choice (fact_id, choice_key) VALUES ($fid, $ckey);";
        command.Parameters.AddWithValue("$fid", factId);
        command.Parameters.AddWithValue("$ckey", choiceKey);
        command.ExecuteNonQuery();
    }

}
