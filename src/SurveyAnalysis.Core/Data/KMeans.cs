using System;
using System.Collections.Generic;

namespace SurveyAnalysis.Data;

// A dependency-free spherical k-means for clustering embedding vectors into topics. Vectors are
// L2-normalised, so Euclidean geometry on the unit sphere matches cosine similarity (the measure topic
// assignment uses). Initialisation is k-means++ over cosine distance; the cluster count is chosen
// automatically by maximising the mean silhouette over k = 2 .. min(⌊√n⌋, maxK). A fixed seed keeps the
// result deterministic (important for tests and for a stable dictionary across re-runs).
public static class KMeans
{
    // The assignment (cluster index per input vector) and the unit-length centroids.
    public sealed record Result(int[] Assignments, IReadOnlyList<float[]> Centroids);

    // Clusters the vectors, picking k automatically. Returns one cluster for fewer than four vectors
    // (too few to compare partitions by silhouette). Vectors must be non-empty and equal length.
    public static Result Cluster(IReadOnlyList<float[]> vectors, int maxK = 12, int seed = 1)
    {
        var n = vectors.Count;
        if (n == 0)
            return new Result(Array.Empty<int>(), Array.Empty<float[]>());

        var points = Normalise(vectors);

        // Too few points to meaningfully split: one cluster (its centroid is the mean direction).
        var upperK = Math.Min(maxK, (int)Math.Floor(Math.Sqrt(n)));
        if (n < 4 || upperK < 2)
            return new Result(new int[n], new[] { Mean(points, new int[n], 0, 1)[0] });

        // Search k for the best mean silhouette; keep the best partition.
        Result? best = null;
        var bestScore = double.NegativeInfinity;
        for (var k = 2; k <= Math.Min(upperK, n - 1); k++)
        {
            var (assignments, centroids) = Lloyd(points, k, seed);
            var score = Silhouette(points, assignments, k);
            if (score > bestScore)
            {
                bestScore = score;
                best = new Result(assignments, centroids);
            }
        }
        return best!;
    }

    // One k-means run: k-means++ seeding, then Lloyd iterations until the assignment is stable.
    private static (int[] Assignments, float[][] Centroids) Lloyd(float[][] points, int k, int seed)
    {
        var random = new Random(seed);
        var centroids = SeedPlusPlus(points, k, random);
        var assignments = new int[points.Length];

        for (var iteration = 0; iteration < 50; iteration++)
        {
            var changed = false;

            // Assignment step: each point joins the centroid it is most cosine-similar to (max dot,
            // since everything is unit length).
            for (var i = 0; i < points.Length; i++)
            {
                var nearest = NearestCentroid(points[i], centroids);
                if (nearest != assignments[i])
                {
                    assignments[i] = nearest;
                    changed = true;
                }
            }

            // Update step: each centroid becomes the normalised mean of its members. An emptied cluster
            // is re-seeded to the point currently least similar to its own centroid, so k stays filled.
            centroids = Mean(points, assignments, k, points.Length);
            RefillEmpty(points, assignments, centroids);

            if (!changed && iteration > 0)
                break;
        }

        return (assignments, centroids);
    }

    // k-means++: first centroid random, each next chosen with probability proportional to its squared
    // cosine distance to the nearest chosen centroid (spreads the seeds apart).
    private static float[][] SeedPlusPlus(float[][] points, int k, Random random)
    {
        var centroids = new float[k][];
        centroids[0] = (float[])points[random.Next(points.Length)].Clone();

        var distance = new double[points.Length];
        for (var c = 1; c < k; c++)
        {
            double total = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var nearest = 1.0 - MaxDot(points[i], centroids, c);   // cosine distance to nearest seed
                distance[i] = nearest * nearest;
                total += distance[i];
            }

            // Pick the next seed by the weighted distribution; fall back to the last point if all points
            // coincide with existing seeds (total distance 0).
            var threshold = random.NextDouble() * total;
            var chosen = points.Length - 1;
            double running = 0;
            for (var i = 0; i < points.Length; i++)
            {
                running += distance[i];
                if (running >= threshold && total > 0)
                {
                    chosen = i;
                    break;
                }
            }
            centroids[c] = (float[])points[chosen].Clone();
        }
        return centroids;
    }

    // The mean silhouette over all points for a partition: (b - a) / max(a, b) per point, where a is the
    // mean distance to same-cluster points and b the smallest mean distance to another cluster. Singleton
    // clusters contribute 0 (no within-cluster neighbours). Distance is cosine distance (1 - dot).
    private static double Silhouette(float[][] points, int[] assignments, int k)
    {
        var n = points.Length;
        var sizes = new int[k];
        foreach (var a in assignments)
            sizes[a]++;

        double total = 0;
        for (var i = 0; i < n; i++)
        {
            // Mean distance from i to every cluster (including its own).
            var sum = new double[k];
            for (var j = 0; j < n; j++)
                if (j != i)
                    sum[assignments[j]] += 1.0 - Dot(points[i], points[j]);

            var own = assignments[i];
            if (sizes[own] <= 1)
                continue;   // singleton → silhouette 0

            var a = sum[own] / (sizes[own] - 1);
            var b = double.PositiveInfinity;
            for (var c = 0; c < k; c++)
                if (c != own && sizes[c] > 0)
                    b = Math.Min(b, sum[c] / sizes[c]);

            if (double.IsInfinity(b))
                continue;
            var denom = Math.Max(a, b);
            if (denom > 0)   // coincident points (a = b = 0) contribute 0, not NaN
                total += (b - a) / denom;
        }
        return total / n;
    }

    // ===== vector helpers (all vectors unit length) =====

    private static float[][] Normalise(IReadOnlyList<float[]> vectors)
    {
        var result = new float[vectors.Count][];
        for (var i = 0; i < vectors.Count; i++)
        {
            var v = vectors[i];
            double norm = 0;
            foreach (var x in v)
                norm += x * (double)x;
            norm = Math.Sqrt(norm);
            var unit = new float[v.Length];
            if (norm > 0)
                for (var j = 0; j < v.Length; j++)
                    unit[j] = (float)(v[j] / norm);
            result[i] = unit;
        }
        return result;
    }

    // The normalised mean direction of each cluster (the centroid). Empty clusters get a zero vector,
    // which RefillEmpty then replaces.
    private static float[][] Mean(float[][] points, int[] assignments, int k, int _)
    {
        var dimension = points.Length > 0 ? points[0].Length : 0;
        var clusters = Math.Max(k, 1);
        var sums = new double[clusters][];
        for (var c = 0; c < clusters; c++)
            sums[c] = new double[dimension];

        for (var i = 0; i < points.Length; i++)
        {
            var cluster = assignments[i];
            for (var d = 0; d < dimension; d++)
                sums[cluster][d] += points[i][d];
        }

        var centroids = new float[clusters][];
        for (var c = 0; c < clusters; c++)
        {
            double norm = 0;
            foreach (var x in sums[c])
                norm += x * x;
            norm = Math.Sqrt(norm);
            var unit = new float[dimension];
            if (norm > 0)
                for (var d = 0; d < dimension; d++)
                    unit[d] = (float)(sums[c][d] / norm);
            centroids[c] = unit;
        }
        return centroids;
    }

    // Re-seeds any centroid that ended up zero (an emptied cluster) to the point least similar to its
    // assigned centroid, so the next iteration can split it off.
    private static void RefillEmpty(float[][] points, int[] assignments, float[][] centroids)
    {
        for (var c = 0; c < centroids.Length; c++)
        {
            if (!IsZero(centroids[c]))
                continue;
            var worst = 0;
            var worstSimilarity = double.PositiveInfinity;
            for (var i = 0; i < points.Length; i++)
            {
                var similarity = Dot(points[i], centroids[assignments[i]]);
                if (similarity < worstSimilarity)
                {
                    worstSimilarity = similarity;
                    worst = i;
                }
            }
            centroids[c] = (float[])points[worst].Clone();
        }
    }

    private static int NearestCentroid(float[] point, float[][] centroids)
    {
        var best = 0;
        var bestDot = double.NegativeInfinity;
        for (var c = 0; c < centroids.Length; c++)
        {
            var dot = Dot(point, centroids[c]);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = c;
            }
        }
        return best;
    }

    // The largest dot product between a point and the first `count` centroids (their cosine similarity,
    // as both are unit length).
    private static double MaxDot(float[] point, float[][] centroids, int count)
    {
        var best = double.NegativeInfinity;
        for (var c = 0; c < count; c++)
            best = Math.Max(best, Dot(point, centroids[c]));
        return best;
    }

    private static double Dot(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < length; i++)
            dot += a[i] * (double)b[i];
        return dot;
    }

    private static bool IsZero(float[] v)
    {
        foreach (var x in v)
            if (x != 0)
                return false;
        return true;
    }
}
