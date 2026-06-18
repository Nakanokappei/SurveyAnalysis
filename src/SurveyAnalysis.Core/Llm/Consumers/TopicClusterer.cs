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
// them (k chosen automatically), then name each cluster from a few representative answers with the chat
// model. Returns one (label, centroid) per cluster — the caller persists them via
// TopicRepository.ReplaceTopics, after which TopicAssigner can route new answers to the nearest centroid.
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

    // Clusters the column's answers into named topics. Needs at least two distinct non-empty answers;
    // returns an empty list otherwise (the caller tells the user there is too little data). Progress is
    // reported per cluster named (total = cluster count) once clustering is done.
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

        // Group answer indices by cluster, dropping any empty cluster.
        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < clustering.Assignments.Length; i++)
            (members.TryGetValue(clustering.Assignments[i], out var list)
                ? list
                : members[clustering.Assignments[i]] = new List<int>()).Add(i);

        var topics = new List<Topic>();
        var usedLabels = new HashSet<string>();
        var total = members.Count;
        var done = 0;
        foreach (var (cluster, indices) in members.OrderBy(m => m.Key))
        {
            ct.ThrowIfCancellationRequested();
            var centroid = clustering.Centroids[cluster];
            var representatives = TopByCentroid(indices, vectors, centroid)
                .Select(i => distinct[i])
                .ToList();

            var label = await NameClusterAsync(representatives, ct).ConfigureAwait(false);
            topics.Add(new Topic(MakeUnique(label, usedLabels), centroid));

            done++;
            progress?.Report((done, total));
        }
        return topics;
    }

    // The cluster's answers ordered by how close they are to the centroid (most representative first),
    // capped at RepresentativesPerCluster.
    private static IEnumerable<int> TopByCentroid(List<int> indices, List<float[]> vectors, float[] centroid) =>
        indices
            .OrderByDescending(i => Cosine(vectors[i], centroid))
            .Take(RepresentativesPerCluster);

    // Names one cluster from its representative answers. JSON mode keeps parsing robust; an unparseable
    // or empty reply falls back to a generic label (deduplicated by the caller).
    private async Task<string> NameClusterAsync(IReadOnlyList<string> representatives, CancellationToken ct)
    {
        var prompt = new StringBuilder("次の回答はすべて同じトピックに属します。\n");
        foreach (var text in representatives)
            prompt.Append("- ").AppendLine(text);

        var request = new ChatRequest(
            _namingModel,
            new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", prompt.ToString()),
            },
            Temperature: 0,
            ResponseFormat: "json_object");

        var result = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
        return ParseLabel(result.Content);
    }

    private const string SystemPrompt =
        "あなたはアンケートの自由記述を分類するアシスタントです。" +
        "与えられた回答群に共通するトピックを表す、短い日本語のラベル（体言止め・10文字程度）を1つ付けてください。" +
        "出力は {\"label\": \"...\"} の形のJSONのみ。ラベル以外は出力しないこと。";

    private static string ParseLabel(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("label", out var label)
                && label.ValueKind == JsonValueKind.String)
            {
                var text = label.GetString()!.Trim();
                if (text.Length > 0)
                    return text;
            }
        }
        catch (JsonException)
        {
            // fall through to the generic label
        }
        return "トピック";
    }

    // Keeps labels unique within the column: a clash gets a numeric suffix (foo, foo (2), foo (3)).
    private static string MakeUnique(string label, HashSet<string> used)
    {
        var candidate = label;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = $"{label} ({suffix++})";
        return candidate;
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
