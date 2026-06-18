using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Data;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The import analyzer scores sentiment per response (row + column) and assigns each 自由記述 to the
// nearest topic, persisting to the raw tables which Rebuild then projects into the star.
public class ImportAnalyzerTests
{
    [Fact]
    public async Task Analyze_then_Rebuild_projects_sentiment_and_topic()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        var freeText = new DataField { Name = "ご意見", FieldType = FieldType.FreeText };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        project.Fields.Add(freeText);
        projects.Insert(project);
        responses.InsertResponses(project.Id, "t", new[]
        {
            new SurveyResponse { Answers = new[] { new FieldAnswer("記入日", "2026/05/20"), new FieldAnswer("ご意見", "とても良い対応でした") } },
        });

        // A dictionary topic with a centroid so the (fake) embedding assigns to it.
        topics.ReplaceTopics(freeText.Id, new (string, float[]?)[] { ("満足", new[] { 1f, 0f, 0f }) });

        var progressReports = new List<(int, int)>();
        var analyzer = new ImportAnalyzer(new FakeLlm(score: 0.5), responses, topics, results, "m");
        await analyzer.AnalyzeAsync(project, new SyncProgress(progressReports));

        analytics.Rebuild(project);

        Assert.Equal(0.5, ScalarDouble(temp, "SELECT sentiment_score FROM fact_response;"));
        Assert.Equal("満足", ScalarString(temp, "SELECT t.label FROM fact_response_topic frt JOIN dim_topic t ON t.topic_key = frt.topic_key;"));
        Assert.Contains((1, 1), progressReports);   // one response, reported done
    }

    [Fact]
    public async Task Analyze_is_a_noop_without_free_text_fields()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "満足度", FieldType = FieldType.Choice });
        projects.Insert(project);
        responses.InsertResponses(project.Id, "t", new[] { new SurveyResponse { Answers = new[] { new FieldAnswer("満足度", "満足") } } });

        Assert.False(ImportAnalyzer.HasAnalyzableFields(project));
        await new ImportAnalyzer(new FakeLlm(score: 0.5), responses, new TopicRepository(temp.Db), new AnalysisResultsRepository(temp.Db), "m").AnalyzeAsync(project);

        Assert.Equal(0L, ScalarLong(temp, "SELECT COUNT(*) FROM response_sentiment;"));
    }

    private static long ScalarLong(TempDatabase temp, string sql) => (long)Scalar(temp, sql)!;
    private static double ScalarDouble(TempDatabase temp, string sql) => (double)Scalar(temp, sql)!;
    private static string ScalarString(TempDatabase temp, string sql) => (string)Scalar(temp, sql)!;

    private static object? Scalar(TempDatabase temp, string sql)
    {
        using var connection = temp.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // A fake LLM: chat returns a fixed sentiment JSON; embeddings return a fixed vector (so the nearest
    // topic is deterministic).
    private sealed class FakeLlm : ILlmClient
    {
        private readonly double _score;
        public FakeLlm(double score) => _score = score;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResult($"{{\"score\":{_score},\"negative\":false}}", request.Model, null, null, false));

        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<EmbeddingVector>)inputs.Select(_ => new EmbeddingVector(new[] { 1f, 0f, 0f }, false)).ToList());
    }

    // Synchronous IProgress so reports are captured deterministically in the test.
    private sealed class SyncProgress : System.IProgress<(int, int)>
    {
        private readonly List<(int, int)> _reports;
        public SyncProgress(List<(int, int)> reports) => _reports = reports;
        public void Report((int, int) value) => _reports.Add(value);
    }
}
