using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Data;

namespace SurveyAnalysis.Llm.Consumers;

// Builds a column's topic dictionary from its existing 自由記述 answers: embed the distinct texts, cluster
// them (k chosen automatically), then name every cluster in ONE chat call so the model sees the whole
// topic space at once. Naming the clusters together lets it (a) give clearly-distinct labels to distinct
// topics and (b) consolidate near-duplicate clusters by handing them the same label — k-means tends to
// over-split short opinions, and per-cluster naming produced look-alike topics (対応の質 / 工事の品質).
// Clusters that share a final label are merged into one topic (their member vectors form the centroid).
// Returns one (label, centroid) per consolidated topic; the caller persists them via
// TopicRepository.ReplaceTopics, after which TopicAssigner routes new answers to the nearest centroid.
// Embeddings and naming both go through the shared ILlmClient (cached), so re-running is cheap.
public sealed class TopicClusterer
{
    private readonly ILlmClient _llm;
    private readonly string _namingModel;

    // How many of a cluster's answers (those nearest its centroid) are shown to the model when naming it.
    private const int RepresentativesPerCluster = 6;

    public TopicClusterer(ILlmClient llm, string namingModel)
    {
        _llm = llm;
        _namingModel = namingModel;
    }

    // A built topic: its label and the unit-length centroid used for later nearest-topic assignment.
    public sealed record Topic(string Label, float[] Centroid);

    // Clusters the column's answers into named, consolidated topics. Needs at least two distinct non-empty
    // answers; returns an empty list otherwise (the caller tells the user there is too little data).
    // Progress is reported per consolidated topic once naming is done.
    public async Task<IReadOnlyList<Topic>> BuildTopicsAsync(
        IReadOnlyList<string> texts,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var distinct = texts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct()
            .ToList();
        if (distinct.Count < 2)
            return Array.Empty<Topic>();

        // Embed every distinct answer (one batched, cached call), then cluster the vectors.
        var vectors = (await _llm.EmbedAsync(distinct, ct).ConfigureAwait(false))
            .Select(v => v.Values)
            .ToList();
        var clustering = KMeans.Cluster(vectors);
        var k = clustering.Centroids.Count;

        // Group the answer indices by cluster (dropping any empty cluster), keeping the cluster index so
        // the naming response can be matched back.
        var members = new List<int>[k];
        for (var c = 0; c < k; c++)
            members[c] = new List<int>();
        for (var i = 0; i < clustering.Assignments.Length; i++)
            members[clustering.Assignments[i]].Add(i);

        var clusters = new List<(int Index, IReadOnlyList<string> Representatives)>();
        for (var c = 0; c < k; c++)
            if (members[c].Count > 0)
                clusters.Add((c, TopByCentroid(members[c], vectors, clustering.Centroids[c]).Select(i => distinct[i]).ToList()));

        // Name (and implicitly consolidate) every cluster in a single call.
        var labelByCluster = await NameClustersAsync(clusters, ct).ConfigureAwait(false);

        // Each distinct final label is one topic; clusters that share a label merge (their member vectors
        // become the consolidated centroid). LINQ GroupBy keeps first-seen label order, so the dictionary
        // is stable across re-runs (the clustering is seeded).
        var groups = clusters
            .GroupBy(cluster => labelByCluster.TryGetValue(cluster.Index, out var label) ? label : $"トピック{cluster.Index + 1}")
            .ToList();

        var topics = new List<Topic>();
        var total = groups.Count;
        var done = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var indices = group.SelectMany(cluster => members[cluster.Index]).ToList();
            topics.Add(new Topic(group.Key, NormalizedMean(vectors, indices)));
            done++;
            progress?.Report((done, total));
        }
        return topics;
    }

    // The cluster's answers ordered by how close they are to the centroid (most representative first),
    // capped at RepresentativesPerCluster.
    private static IEnumerable<int> TopByCentroid(IReadOnlyList<int> indices, IReadOnlyList<float[]> vectors, float[] centroid) =>
        indices
            .OrderByDescending(i => Cosine(vectors[i], centroid))
            .Take(RepresentativesPerCluster);

    // Names every cluster at once: the model gets each cluster's representative answers and returns a label
    // per cluster index, merging clusters it judges to be the same topic by giving them the same label.
    // JSON mode keeps parsing robust; an unparseable / missing entry falls back to a per-cluster generic
    // label (so the cluster survives as its own topic rather than vanishing).
    private async Task<IReadOnlyDictionary<int, string>> NameClustersAsync(
        IReadOnlyList<(int Index, IReadOnlyList<string> Representatives)> clusters, CancellationToken ct)
    {
        if (clusters.Count == 0)
            return new Dictionary<int, string>();

        var request = new ChatRequest(
            _namingModel,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", BuildNamingPrompt(clusters)),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
        return ParseLabels(result.Content, clusters.Max(c => c.Index) + 1);
    }

    private const string SystemPrompt =
        "あなたはアンケート自由記述のトピックを整理する専門家です。各グループの代表回答を読み、グループごとに" +
        "短い日本語のトピック名（体言止め・10文字程度）を付けてください。" +
        "最重要：意味が実質的に同じグループには同じトピック名を付けて1つに統合し、異なるトピックには互いに" +
        "明確に区別できる名前を付けてください。似たトピックを安易に分けず、できるだけ少ないトピック数にまとめます。" +
        "出力は {\"labels\": {\"0\": \"名前\", \"1\": \"名前\", ...}} の形式のJSONのみ（キーはグループ番号）。";

    // Lists each cluster (by its index) with its representative answers, so the model can compare every
    // group before labelling — this is what lets it produce distinct labels and merge duplicates.
    private static string BuildNamingPrompt(IReadOnlyList<(int Index, IReadOnlyList<string> Representatives)> clusters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("以下は、アンケート自由記述をクラスタリングした各グループの代表回答です。");
        sb.AppendLine("全グループを見比べ、グループ番号をキーにしたトピック名のJSONを返してください。");
        sb.AppendLine();
        foreach (var (index, representatives) in clusters)
        {
            sb.Append("グループ").Append(index).AppendLine(":");
            foreach (var representative in representatives)
                sb.Append("- ").AppendLine(representative);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Reads the {"labels": {"<index>": "<name>"}} map (tolerating a flat root) into a cluster-index→label
    // dictionary, keeping only non-empty string labels for known cluster indices.
    private static IReadOnlyDictionary<int, string> ParseLabels(string json, int clusterCount)
    {
        var labels = new Dictionary<int, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return labels;

            var map = root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object ? l : root;
            for (var i = 0; i < clusterCount; i++)
                if (map.TryGetProperty(i.ToString(), out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!.Trim();
                    if (text.Length > 0)
                        labels[i] = text;
                }
        }
        catch (JsonException)
        {
            // Unparseable model output → no labels; the caller falls back to per-cluster generic names.
        }
        return labels;
    }

    // The unit-length mean direction of the given member vectors (each normalised first, then averaged and
    // re-normalised) — the consolidated topic's centroid, matching the spherical k-means centroid geometry
    // that TopicAssigner compares against by cosine.
    private static float[] NormalizedMean(IReadOnlyList<float[]> vectors, IReadOnlyList<int> indices)
    {
        var dimension = vectors[indices[0]].Length;
        var sum = new double[dimension];
        foreach (var i in indices)
        {
            var vector = vectors[i];
            double norm = 0;
            for (var d = 0; d < vector.Length && d < dimension; d++)
                norm += vector[d] * (double)vector[d];
            norm = Math.Sqrt(norm);
            if (norm > 0)
                for (var d = 0; d < dimension; d++)
                    sum[d] += vector[d] / norm;
        }

        double total = 0;
        foreach (var x in sum)
            total += x * x;
        total = Math.Sqrt(total);

        var unit = new float[dimension];
        if (total > 0)
            for (var d = 0; d < dimension; d++)
                unit[d] = (float)(sum[d] / total);
        return unit;
    }

    private static double Cosine(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }
        if (normA == 0 || normB == 0)
            return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
