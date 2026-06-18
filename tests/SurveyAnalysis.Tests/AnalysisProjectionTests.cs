using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// Rebuild projects the persisted LLM analysis (response_sentiment / response_topic / field_topics) into
// the star: fact_response.sentiment_score + main_topic_key, dim_topic (per field), and the
// fact_response_topic bridge. Rebuild itself never calls the LLM — it only reads the raw tables.
public class AnalysisProjectionTests
{
    [Fact]
    public void Rebuild_projects_sentiment_and_topic_into_the_star()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var topics = new TopicRepository(temp.Db);
        var results = new AnalysisResultsRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        var dateField = new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true };
        var freeText = new DataField { Name = "ご意見", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment };
        project.Fields.Add(dateField);
        project.Fields.Add(freeText);
        projects.Insert(project);

        responses.InsertResponses(project.Id, "test", new[]
        {
            new SurveyResponse { Answers = new[] { new FieldAnswer("記入日", "2026/05/20"), new FieldAnswer("ご意見", "丁寧で良かった。") } },
        });
        var responseId = ScalarLong(temp, "SELECT id FROM responses;");

        // Persisted analysis: a dictionary topic, the row sentiment, and the column's topic + sentiment.
        var topicId = topics.AddTopic(freeText.Id, "対応の良さ");
        results.SaveRowSentiment(responseId, 0.8, isNegative: false);
        results.SaveTopicAssignment(responseId, freeText.Id, topicId, 0.8, isNegative: false);

        analytics.Rebuild(project);

        // The fact carries the row sentiment and a non-null main topic.
        Assert.Equal(0.8, ScalarDouble(temp, "SELECT sentiment_score FROM fact_response;"));
        Assert.Equal(0L, ScalarLong(temp, "SELECT COUNT(*) FROM fact_response WHERE main_topic_key IS NULL;"));
        // dim_topic carries the per-field topic and the bridge links the fact to it.
        Assert.Equal("対応の良さ", ScalarString(temp, "SELECT label FROM dim_topic;"));
        Assert.Equal(freeText.Id, ScalarLong(temp, "SELECT field_id FROM dim_topic;"));
        Assert.Equal(1L, ScalarLong(temp, "SELECT COUNT(*) FROM fact_response_topic WHERE topic_key IS NOT NULL;"));
    }

    [Fact]
    public void Rebuild_leaves_sentiment_and_topic_null_when_unanalysed()
    {
        using var temp = new TempDatabase();
        var projects = new ProjectRepository(temp.Db);
        var responses = new ResponseRepository(temp.Db);
        var analytics = new AnalyticsRepository(temp.Db);

        var project = new Project { Name = "P" };
        project.Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, UseForAggregation = true });
        projects.Insert(project);
        responses.InsertResponses(project.Id, "t", new[] { new SurveyResponse { Answers = new[] { new FieldAnswer("記入日", "2026/05/20") } } });

        analytics.Rebuild(project);

        Assert.Equal(1L, ScalarLong(temp, "SELECT COUNT(*) FROM fact_response WHERE sentiment_score IS NULL AND main_topic_key IS NULL;"));
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
}
