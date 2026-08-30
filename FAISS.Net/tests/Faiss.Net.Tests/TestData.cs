using Faiss.Net;

namespace Faiss.Net.Tests;

/// <summary>
/// Shared fixtures. Every dataset is seeded, so a failure reproduces exactly rather than appearing
/// once in ten runs — which matters here because approximate indexes are allowed to be wrong
/// occasionally, and a flaky recall assertion is indistinguishable from a real regression.
/// </summary>
internal static class TestData
{
    /// <summary>Clustered data: the shape approximate indexes are actually designed for.</summary>
    public static float[] Clustered(int n, int d, int clusters = 16, long seed = 42) =>
        FaissNet.RandomClusteredVectors(n, d, clusters, 0.08f, seed);

    /// <summary>Uniform data, for tests that only care about exactness.</summary>
    public static float[] Uniform(int n, int d, long seed = 42) => FaissNet.RandomVectors(n, d, seed);

    /// <summary>Exact neighbours, used as ground truth for recall assertions.</summary>
    public static SearchResult GroundTruth(float[] database, float[] queries, int d, int k, MetricType metric = MetricType.L2)
    {
        var flat = new IndexFlat(d, metric);
        flat.Add(database);
        return flat.Search(queries, k);
    }

    /// <summary>First <paramref name="count"/> vectors of a dataset, as a query batch.</summary>
    public static float[] Slice(float[] data, int d, int count) => data.AsSpan(0, count * d).ToArray();
}
