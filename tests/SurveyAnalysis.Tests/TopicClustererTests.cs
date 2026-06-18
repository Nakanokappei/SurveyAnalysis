using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using Xunit;

namespace SurveyAnalysis.Tests;

// The topic clusterer embeds a column's answers, clusters them, and names each cluster. The fake LLM
// places answers in two embedding groups by keyword and names a cluster from the keyword in its
// representative answers, so the expected topics are deterministic.
public class TopicClustererTests
{
    [Fact]
    public async Task BuildTopics_clusters_and_names_two_groups()
    {
        var texts = new List<string>
        {
            "配線がきれいだった", "配線の処理が丁寧", "配線をまとめてくれた", "配線が見えない工夫",
            "対応が丁寧だった", "対応がよかった", "対応が早い", "対応に満足",
        };

        var topics = await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(texts);

        Assert.Equal(2, topics.Count);
        Assert.Equal(new[] { "対応", "配線" }, topics.Select(t => t.Label).OrderBy(l => l).ToArray());
        Assert.All(topics, t => Assert.True(t.Centroid.Length == 3));
    }

    [Fact]
    public async Task BuildTopics_returns_empty_for_too_few_answers()
    {
        var topics = await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(new[] { "ひとつだけ" });
        Assert.Empty(topics);
    }

    [Fact]
    public async Task BuildTopics_deduplicates_colliding_labels()
    {
        var texts = new List<string>
        {
            "配線がきれいだった", "配線の処理が丁寧", "配線をまとめてくれた", "配線が見えない工夫",
            "対応が丁寧だった", "対応がよかった", "対応が早い", "対応に満足",
        };

        // A namer that always returns the same label forces the within-column uniqueness suffix.
        var topics = await new TopicClusterer(new ConstantNameLlm("共通"), "m").BuildTopicsAsync(texts);

        Assert.Equal(2, topics.Count);
        Assert.Equal(new[] { "共通", "共通 (2)" }, topics.Select(t => t.Label).ToArray());
    }

    [Fact]
    public async Task BuildTopics_reports_progress_per_cluster()
    {
        var texts = new List<string>
        {
            "配線がきれいだった", "配線の処理が丁寧", "配線をまとめてくれた", "配線が見えない工夫",
            "対応が丁寧だった", "対応がよかった", "対応が早い", "対応に満足",
        };
        var reports = new List<(int, int)>();

        await new TopicClusterer(new KeywordLlm(), "m").BuildTopicsAsync(texts, new SyncProgress(reports));

        Assert.Contains((2, 2), reports);
    }

    // Embeds by keyword (配線→axis0, 対応→axis1) and names a cluster from the keyword its representative
    // answers contain — so two keyword groups become two distinctly named topics.
    private sealed class KeywordLlm : ILlmClient
    {
        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<EmbeddingVector>)inputs.Select(Vector).ToList());

        private static EmbeddingVector Vector(string text) => new(
            text.Contains("配線") ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f },
            false);

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
        {
            var prompt = request.Messages[^1].Content;
            var label = prompt.Contains("配線") ? "配線" : "対応";
            return Task.FromResult(new ChatResult($"{{\"label\":\"{label}\"}}", request.Model, null, null, false));
        }
    }

    // Embeds the same two keyword groups but always names every cluster the same, to exercise dedup.
    private sealed class ConstantNameLlm : ILlmClient
    {
        private readonly string _label;
        public ConstantNameLlm(string label) => _label = label;

        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<EmbeddingVector>)inputs
                .Select(t => new EmbeddingVector(t.Contains("配線") ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f }, false))
                .ToList());

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResult($"{{\"label\":\"{_label}\"}}", request.Model, null, null, false));
    }

    private sealed class SyncProgress : IProgress<(int, int)>
    {
        private readonly List<(int, int)> _reports;
        public SyncProgress(List<(int, int)> reports) => _reports = reports;
        public void Report((int, int) value) => _reports.Add(value);
    }
}
