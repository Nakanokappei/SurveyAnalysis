using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.Llm.Consumers;

// Assigns 自由記述 texts to the nearest topic in a column's dictionary by embedding-space cosine
// similarity. Only topics that have a centroid (built by clustering) participate; if a column has no
// centroids yet, every text is left unassigned (null). Texts are embedded in one batched call.
public sealed class TopicAssigner
{
    private readonly IEmbeddingClient _embed;

    public TopicAssigner(IEmbeddingClient embed) => _embed = embed;

    // One assigned topic id (field_topics.id) per input text, or null when there is no usable centroid.
    public async Task<IReadOnlyList<long?>> AssignAsync(IReadOnlyList<string> texts, IReadOnlyList<FieldTopic> topics, CancellationToken ct = default)
    {
        var candidates = topics.Where(t => t.Centroid is { Length: > 0 }).ToList();
        if (texts.Count == 0 || candidates.Count == 0)
            return texts.Select(_ => (long?)null).ToList();

        var vectors = await _embed.EmbedAsync(texts, ct).ConfigureAwait(false);
        return vectors.Select(v => Nearest(v.Values, candidates)).ToList();
    }

    // The id of the candidate whose centroid is most cosine-similar to the vector.
    private static long? Nearest(float[] vector, List<FieldTopic> candidates)
    {
        long? best = null;
        var bestSimilarity = double.NegativeInfinity;
        foreach (var topic in candidates)
        {
            var similarity = Cosine(vector, topic.Centroid!);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = topic.Id;
            }
        }
        return best;
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
