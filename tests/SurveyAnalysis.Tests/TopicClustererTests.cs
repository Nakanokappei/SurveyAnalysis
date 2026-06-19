using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using Xunit;

namespace SurveyAnalysis.Tests;

// The topic clusterer embeds a column's answers, clusters them, and names every cluster in one call so the
// model can give distinct labels and merge duplicate clusters. The fake LLM places answers in two
// embedding groups by keyword and labels each cluster from the keyword in its block, so the expected
// topics are deterministic.
public class TopicClustererTests
{
    private static readonly string[] TwoGroups =
    {
        "配線がきれいだった", "配線の処理が丁寧", "配線をまとめてくれた", "配線が見えない工夫",
        "対応が丁寧だった", "対応がよかった", "対応が早い", "対応に満足",
    };

    [Fact]
    public async Task BuildTopics_clusters_and_names_two_groups()
    {
        var topics = await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(TwoGroups);

        Assert.Equal(2, topics.Count);
        Assert.Equal(new[] { "対応", "配線" }, topics.Select(t => t.Label).OrderBy(l => l).ToArray());
        Assert.All(topics, t => Assert.Equal(3, t.Centroid.Length));
    }

    [Fact]
    public async Task BuildTopics_returns_empty_for_too_few_answers()
    {
        var topics = await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(new[] { "ひとつだけ" });
        Assert.Empty(topics);
    }

    [Fact]
    public async Task BuildTopics_merges_clusters_the_namer_labels_alike()
    {
        // A namer that labels every cluster the same consolidates them into a single topic (the fix for
        // over-split, look-alike topics), and that topic's centroid is built from every member.
        var topics = await new TopicClusterer(new ConstantNameLlm("共通"), "m").BuildTopicsAsync(TwoGroups);

        var topic = Assert.Single(topics);
        Assert.Equal("共通", topic.Label);
        Assert.Equal(3, topic.Centroid.Length);
    }

    [Fact]
    public async Task BuildTopics_reports_progress_per_topic()
    {
        var reports = new List<(int, int)>();
        await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(TwoGroups, new SyncProgress(reports));
        Assert.Contains((2, 2), reports);   // two distinct labels → two consolidated topics
    }

    // Embeds by keyword (配線→axis0, 対応→axis1) and, from the joint naming prompt, labels each "グループN"
    // block by the keyword its representative answers contain — so two keyword groups get two distinct
    // labels and stay separate.
    private sealed class KeywordLlm : ILlmClient
    {
        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<EmbeddingVector>)inputs
                .Select(t => new EmbeddingVector(t.Contains("配線") ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f }, false))
                .ToList());

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResult(LabelByBlock(request.Messages[^1].Content, body => body.Contains("配線") ? "配線" : "対応"), request.Model, null, null, false));
    }

    // Embeds the same two keyword groups but labels every cluster the same, to exercise consolidation.
    private sealed class ConstantNameLlm : ILlmClient
    {
        private readonly string _label;
        public ConstantNameLlm(string label) => _label = label;

        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<EmbeddingVector>)inputs
                .Select(t => new EmbeddingVector(t.Contains("配線") ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f }, false))
                .ToList());

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResult(LabelByBlock(request.Messages[^1].Content, _ => _label), request.Model, null, null, false));
    }

    // Parses the joint naming prompt's "グループN:" blocks and builds a {"labels":{...}} reply, choosing
    // each cluster's label from its block body via the given rule.
    private static string LabelByBlock(string prompt, Func<string, string> labelOf)
    {
        var labels = new List<string>();
        foreach (Match match in Regex.Matches(prompt, @"グループ(\d+):([\s\S]*?)(?=グループ\d+:|$)"))
            labels.Add($"\"{match.Groups[1].Value}\":\"{labelOf(match.Groups[2].Value)}\"");
        return "{\"labels\":{" + string.Join(",", labels) + "}}";
    }

    private sealed class SyncProgress : IProgress<(int, int)>
    {
        private readonly List<(int, int)> _reports;
        public SyncProgress(List<(int, int)> reports) => _reports = reports;
        public void Report((int, int) value) => _reports.Add(value);
    }
}
