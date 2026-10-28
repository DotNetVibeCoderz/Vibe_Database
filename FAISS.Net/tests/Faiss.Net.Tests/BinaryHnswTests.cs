using Faiss.Net.Binary;
using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Binary HNSW: the shared graph engine specialized for Hamming distance.</summary>
public class BinaryHnswTests
{
    private const int Bits = 256;

    /// <summary>Sign bits of clustered floats: binary codes with real neighbourhood structure.</summary>
    private static (byte[] Codes, byte[] Queries) Dataset(int n = 4000, int nq = 50)
    {
        var floats = FaissNet.RandomClusteredVectors(n + nq, Bits, 32, 0.3f, 13);
        var all = new byte[(n + nq) * (Bits / 8)];
        for (int i = 0; i < n + nq; i++)
        {
            var row = floats.AsSpan(i * Bits, Bits).ToArray();
            float mean = row.Average();
            HammingOps.Binarize(row, all.AsSpan(i * (Bits / 8), Bits / 8), mean);
        }
        return (all.AsSpan(0, n * (Bits / 8)).ToArray(), all.AsSpan(n * (Bits / 8)).ToArray());
    }

    /// <summary>Fraction of results no farther than the exact k-th distance — ties make id-based recall meaningless.</summary>
    private static double DistanceRecall(SearchResult exact, SearchResult found, int k)
    {
        int hits = 0;
        for (int q = 0; q < exact.QueryCount; q++)
        {
            float kth = exact.DistancesFor(q)[k - 1];
            foreach (float distance in found.DistancesFor(q)) if (distance <= kth) hits++;
        }
        return hits / (double)(exact.QueryCount * k);
    }

    [Fact]
    public void BinaryHnswMatchesExactSearchClosely()
    {
        var (codes, queries) = Dataset();
        var flat = new IndexBinaryFlat(Bits);
        flat.Add(codes);
        var hnsw = new IndexBinaryHNSW(Bits, m: 16) { EfSearch = 64 };
        hnsw.Add(codes);

        double recall = DistanceRecall(flat.Search(queries, 10), hnsw.Search(queries, 10), 10);
        Assert.True(recall > 0.9, $"binary HNSW distance-recall@10 was {recall:P1}");
    }

    [Fact]
    public void BinarySelectorsReturnOnlyMembers()
    {
        var (codes, queries) = Dataset(2000, 10);
        var sel = IDSelector.Range(0, 800);
        var flat = new IndexBinaryFlat(Bits);
        flat.Add(codes);
        var hnsw = new IndexBinaryHNSW(Bits, m: 16) { EfSearch = 64 };
        hnsw.Add(codes);

        Assert.All(flat.Search(queries, 10, sel).Labels, id => Assert.InRange(id, 0, 799));
        Assert.All(hnsw.Search(queries, 10, sel).Labels, id => Assert.InRange(id, 0, 799));
    }

    [Fact]
    public void BinaryHnswRoundTripsAndRepairsSinks()
    {
        var (codes, queries) = Dataset(3000, 20);
        var hnsw = new IndexBinaryHNSW(Bits, m: 8) { EfSearch = 8 };
        hnsw.Add(codes);
        var stats = hnsw.AddLevel0LinksFromSample(codes);
        Assert.True(stats.SinksRemaining <= stats.SinksFound, stats.ToString());

        string path = Path.Combine(Path.GetTempPath(), $"faissnet-bhnsw-{Guid.NewGuid():N}.index");
        try
        {
            IndexIO.WriteBinaryIndex(hnsw, path);
            var reloaded = Assert.IsType<IndexBinaryHNSW>(IndexIO.ReadBinaryIndex(path));
            Assert.Equal(hnsw.Search(queries, 10).Labels, reloaded.Search(queries, 10).Labels);
            Assert.Equal(8, reloaded.M);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
