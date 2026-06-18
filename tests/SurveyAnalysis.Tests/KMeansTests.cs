using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

// The dependency-free k-means picks k by silhouette and groups cosine-similar vectors together. Inputs
// are deliberately well-separated so the expected partition is unambiguous and the (seeded) result is
// deterministic.
public class KMeansTests
{
    [Fact]
    public void Cluster_separates_two_well_separated_groups()
    {
        // Five vectors pointing along axis 0, five along axis 1 (tiny jitter so they are not identical).
        var vectors = new List<float[]>();
        for (var i = 0; i < 5; i++)
            vectors.Add(new[] { 1f, 0.01f * i, 0f });
        for (var i = 0; i < 5; i++)
            vectors.Add(new[] { 0f, 1f, 0.01f * i });

        var result = KMeans.Cluster(vectors);

        Assert.Equal(2, result.Centroids.Count);
        // Each group lands in a single cluster, and the two groups differ.
        var groupA = result.Assignments.Take(5).Distinct().ToList();
        var groupB = result.Assignments.Skip(5).Distinct().ToList();
        Assert.Single(groupA);
        Assert.Single(groupB);
        Assert.NotEqual(groupA[0], groupB[0]);
    }

    [Fact]
    public void Cluster_picks_three_for_three_well_separated_groups()
    {
        // Twelve vectors in three orthogonal directions (four each). ⌊√12⌋ = 3 allows k up to 3.
        var axes = new[] { new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } };
        var vectors = new List<float[]>();
        foreach (var axis in axes)
            for (var i = 0; i < 4; i++)
                vectors.Add(new[] { axis[0] + 0.01f * i, axis[1], axis[2] });

        var result = KMeans.Cluster(vectors);

        Assert.Equal(3, result.Centroids.Count);
        Assert.Equal(3, result.Assignments.Distinct().Count());
        // Each block of four shares one cluster.
        Assert.Single(result.Assignments.Take(4).Distinct());
        Assert.Single(result.Assignments.Skip(4).Take(4).Distinct());
        Assert.Single(result.Assignments.Skip(8).Take(4).Distinct());
    }

    [Fact]
    public void Cluster_returns_single_cluster_for_too_few_points()
    {
        var result = KMeans.Cluster(new List<float[]> { new[] { 1f, 0f }, new[] { 0f, 1f } });

        Assert.Single(result.Centroids);
        Assert.All(result.Assignments, a => Assert.Equal(0, a));
    }

    [Fact]
    public void Cluster_handles_identical_vectors_without_throwing()
    {
        var vectors = Enumerable.Range(0, 8).Select(_ => new[] { 1f, 0f, 0f }).ToList();

        var result = KMeans.Cluster(vectors);

        // No partition is meaningful, but the call must succeed and cover every input.
        Assert.Equal(8, result.Assignments.Length);
    }

    [Fact]
    public void Cluster_returns_empty_for_no_vectors()
    {
        var result = KMeans.Cluster(new List<float[]>());

        Assert.Empty(result.Assignments);
        Assert.Empty(result.Centroids);
    }
}
