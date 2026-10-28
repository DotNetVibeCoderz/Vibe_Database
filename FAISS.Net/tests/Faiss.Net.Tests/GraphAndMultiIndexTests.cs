using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>NSG and the inverted multi-index, the v2.0 index families.</summary>
public class GraphAndMultiIndexTests
{
    private const int D = 32;

    /// <summary>
    /// Overlapping clusters (spread 0.3). NSG is built from a k-NN graph, and on tightly separated
    /// clusters that graph has no edges between clusters at all — see
    /// <see cref="NsgDegradesOnWellSeparatedClustersWhereHnswDoesNot"/>.
    /// </summary>
    private static (float[] Data, float[] Queries, SearchResult Truth) Dataset(int n = 4000, int nq = 100, float spread = 0.3f)
    {
        var all = FaissNet.RandomClusteredVectors(n + nq, D, 32, spread, 21);
        var data = all.AsSpan(0, n * D).ToArray();
        var queries = all.AsSpan(n * D).ToArray();
        return (data, queries, TestData.GroundTruth(data, queries, D, 10));
    }

    [Fact]
    public void NsgReachesHighRecall()
    {
        var (data, queries, truth) = Dataset();
        var index = new IndexNSGFlat(D, r: 32) { SearchL = 64 };
        index.Add(data);
        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.9, $"NSG recall@10 was {recall:P1}");
        Assert.InRange(index.AverageDegree(), 4, 32);
    }

    [Fact]
    public void NsgRecallGrowsWithTheSearchPool()
    {
        var (data, queries, truth) = Dataset();
        var index = new IndexNSGFlat(D, r: 16);
        index.Add(data);
        double low = FaissNet.ComputeRecall(truth, index.Search(queries, 10, new SearchParametersHNSW { EfSearch = 10 }));
        double high = FaissNet.ComputeRecall(truth, index.Search(queries, 10, new SearchParametersHNSW { EfSearch = 128 }));
        Assert.True(high >= low, $"{low:P1} -> {high:P1}");
        Assert.True(high > 0.9, $"NSG recall@10 at L=128 was {high:P1}");
    }

    [Fact]
    public void NsgDegradesOnWellSeparatedClustersWhereHnswDoesNot()
    {
        // Documents a property of the algorithm, not a target: with clusters this far apart the
        // exact k-NN graph is disconnected between clusters, and NSG bridges them only through the
        // single attachment edges of its connectivity pass. HNSW's incremental build keeps
        // long-range links from early insertions and is the right choice for such data.
        var (data, queries, truth) = Dataset(spread: 0.1f);
        var nsg = new IndexNSGFlat(D, r: 32) { SearchL = 64 };
        nsg.Add(data);
        var hnsw = new IndexHNSWFlat(D, m: 16) { EfSearch = 64 };
        hnsw.Add(data);
        double nsgRecall = FaissNet.ComputeRecall(truth, nsg.Search(queries, 10));
        double hnswRecall = FaissNet.ComputeRecall(truth, hnsw.Search(queries, 10));
        Assert.True(hnswRecall > 0.9, $"HNSW recall@10 {hnswRecall:P1}");
        Assert.True(nsgRecall < hnswRecall, $"NSG {nsgRecall:P1} vs HNSW {hnswRecall:P1}");
    }

    [Fact]
    public void EveryNsgNodeIsReachableFromTheEntryPoint()
    {
        var (data, _, _) = Dataset(3000, 1);
        var index = new IndexNSGFlat(D, r: 8);
        index.Add(data);

        var reached = new bool[index.Ntotal];
        var stack = new Stack<int>([index.EnterPoint]);
        reached[index.EnterPoint] = true;
        while (stack.Count > 0)
            foreach (int link in index.Neighbors(stack.Pop()))
            {
                if (link < 0) break;
                if (!reached[link]) { reached[link] = true; stack.Push(link); }
            }
        Assert.All(reached, Assert.True);
    }

    [Fact]
    public void NsgIsBuiltOnceAndRoundTrips()
    {
        var (data, queries, _) = Dataset(2000, 20);
        var index = (IndexNSGFlat)FaissNet.IndexFactory(D, "NSG16");
        index.Add(data);
        Assert.Throws<InvalidOperationException>(() => index.Add(data));

        var reloaded = IndexIO.Deserialize(IndexIO.Serialize(index));
        Assert.Equal(index.Search(queries, 10).Labels, reloaded.Search(queries, 10).Labels);

        var sel = IDSelector.Range(0, 1000);
        Assert.All(index.Search(queries, 10, sel).Labels, id => Assert.InRange(id, -1, 999));
    }

    [Fact]
    public void MultiIndexReturnsCellsInExactOrder()
    {
        var (data, queries, _) = Dataset(3000, 10);
        var imi = new MultiIndexQuantizer(D, 2, 5);
        imi.Train(data);
        Assert.Equal(1024, imi.Ntotal);

        // The multi-sequence walk must agree with scoring all 1024 cells exhaustively.
        var cells = new float[1024 * D];
        imi.ReconstructN(0, 1024, cells);
        var exact = new IndexFlatL2(D);
        exact.Add(cells);

        var fast = imi.Search(queries, 20);
        var slow = exact.Search(queries, 20);
        for (int i = 0; i < fast.Distances.Length; i++)
            Assert.Equal(slow.Distances[i], fast.Distances[i], 2);
    }

    [Theory]
    [InlineData("IMI2x5,Flat")]
    [InlineData("IMI2x5,PQ8")]
    public void IvfOverAMultiIndexTrainsSearchesAndRoundTrips(string description)
    {
        var (data, queries, truth) = Dataset();
        var index = (IndexIVF)FaissNet.IndexFactory(D, description);
        Assert.IsType<MultiIndexQuantizer>(index.Quantizer);
        Assert.Equal(1024, index.Nlist);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 64;

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > (description.EndsWith("Flat") ? 0.85 : 0.4), $"{description} recall@10 was {recall:P1}");

        var reloaded = IndexIO.Deserialize(IndexIO.Serialize(index));
        ((IndexIVF)reloaded).Nprobe = 64;
        Assert.Equal(index.Search(queries, 10).Labels, reloaded.Search(queries, 10).Labels);
    }
}
