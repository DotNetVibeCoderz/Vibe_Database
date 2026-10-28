using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>
/// Filtered search: a selector must be applied inside the scan, so k results really are k members
/// of the subset, and the members returned must be the best members, not merely some members.
/// </summary>
public class FilteredSearchTests
{
    private const int D = 32;
    private const int N = 3000;
    private const int K = 10;

    /// <summary>Exact k-NN restricted to <paramref name="sel"/>, by brute force with no index involved.</summary>
    private static List<long>[] ExactFiltered(float[] data, float[] queries, IDSelector sel, int k = K)
    {
        int n = data.Length / D, nq = queries.Length / D;
        var result = new List<long>[nq];
        for (int q = 0; q < nq; q++)
        {
            var candidates = new List<(long Id, float Distance)>();
            for (int i = 0; i < n; i++)
            {
                if (!sel.IsMember(i)) continue;
                candidates.Add((i, Core.VectorOps.L2Sqr(queries.AsSpan(q * D, D), data.AsSpan(i * D, D))));
            }
            result[q] = candidates.OrderBy(c => c.Distance).ThenBy(c => c.Id).Take(k).Select(c => c.Id).ToList();
        }
        return result;
    }

    private static double FilteredRecall(List<long>[] truth, SearchResult found)
    {
        long hits = 0, total = 0;
        for (int q = 0; q < truth.Length; q++)
        {
            var expected = truth[q].ToHashSet();
            total += expected.Count;
            foreach (long id in found.LabelsFor(q)) if (expected.Contains(id)) hits++;
        }
        return hits / (double)Math.Max(1, total);
    }

    private static void AssertAllMembers(SearchResult found, IDSelector sel)
    {
        foreach (long id in found.Labels)
            if (id >= 0) Assert.True(sel.IsMember(id), $"id {id} is not a member of the selector");
    }

    [Fact]
    public void FlatSelectorReturnsExactlyTheBestMembers()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 20);
        var index = new IndexFlatL2(D);
        index.Add(data);

        var sel = IDSelector.Range(1000, 2000);
        var found = index.Search(queries, K, sel);
        var truth = ExactFiltered(data, queries, sel);

        AssertAllMembers(found, sel);
        for (int q = 0; q < 20; q++) Assert.Equal(truth[q], found.LabelsFor(q).ToArray());
    }

    [Fact]
    public void FilteredSearchStillDeliversKResultsWhereOverFetchingWouldNot()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 10);
        var index = new IndexFlatL2(D);
        index.Add(data);

        // One id in fifty survives; over-fetching 10 and filtering would usually leave 0 or 1.
        var sel = IDSelector.Predicate(id => id % 50 == 7);
        var found = index.Search(queries, K, sel);
        Assert.All(found.Labels, id => Assert.True(id >= 0));
        AssertAllMembers(found, sel);
    }

    [Fact]
    public void IvfSelectorMatchesExactFilteredWhenEveryListIsProbed()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 20);
        var index = new IndexIVFFlat(D, nlist: 16) { Nprobe = 16 };
        index.Train(data);
        index.Add(data);

        var sel = IDSelector.Batch(Enumerable.Range(0, N).Where(i => i % 3 == 0).Select(i => (long)i));
        var found = index.Search(queries, K, sel);
        AssertAllMembers(found, sel);
        Assert.Equal(1.0, FilteredRecall(ExactFiltered(data, queries, sel), found), 6);
    }

    [Fact]
    public void IvfRangeSelectorSkipsListsWithoutLosingMembers()
    {
        // Ids are assigned in add order, so adding cluster by cluster makes a list's id range narrow
        // and a range selector can rule whole lists out from their bounds alone.
        var data = TestData.Clustered(N, D);
        var index = new IndexIVFFlat(D, nlist: 16) { Nprobe = 16 };
        index.Train(data);
        index.Add(data);
        var queries = TestData.Slice(data, D, 20);

        var sel = IDSelector.Range(500, 900);
        var found = index.Search(queries, K, sel);
        AssertAllMembers(found, sel);
        Assert.Equal(1.0, FilteredRecall(ExactFiltered(data, queries, sel), found), 6);
    }

    [Fact]
    public void HnswFilteredSearchKeepsHighRecall()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 50);
        var index = new IndexHNSWFlat(D, m: 16) { EfConstruction = 80 };
        index.Add(data);

        var sel = IDSelector.Predicate(id => id % 4 != 0);
        var found = index.Search(queries, K, new SearchParametersHNSW { Sel = sel, EfSearch = 64 });
        AssertAllMembers(found, sel);
        double recall = FilteredRecall(ExactFiltered(data, queries, sel), found);
        Assert.True(recall > 0.9, $"filtered HNSW recall@10 was {recall:P1}");
    }

    [Theory]
    [InlineData("PQ8")]
    [InlineData("SQ8")]
    [InlineData("IVF16,PQ8")]
    [InlineData("IVF16,SQ8")]
    [InlineData("HNSW16,SQ8")]
    public void CompressedIndexesOnlyReturnMembers(string factory)
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 20);
        var index = FaissNet.IndexFactory(D, factory);
        index.Train(data);
        index.Add(data);

        var sel = IDSelector.Range(0, 1500);
        var found = index.Search(queries, K, sel);
        AssertAllMembers(found, sel);
        Assert.True(found.Labels.Count(id => id >= 0) >= 20 * K * 0.9);
    }

    [Fact]
    public void IdMapSelectorSpeaksCallerIds()
    {
        var data = TestData.Clustered(1000, D);
        var index = new IndexIDMap(new IndexFlatL2(D));
        var ids = Enumerable.Range(0, 1000).Select(i => 100_000L + i * 10).ToArray();
        index.AddWithIds(data, ids);

        var sel = IDSelector.Range(100_000, 105_000); // the first 500 caller ids
        var found = index.Search(TestData.Slice(data, D, 10), K, sel);
        Assert.All(found.Labels, id => Assert.InRange(id, 100_000, 104_999));
    }

    [Fact]
    public void ShardsSelectorWorksAcrossShardBoundariesInParallel()
    {
        var data = TestData.Clustered(1200, D);
        var reference = new IndexFlatL2(D);
        reference.Add(data);

        var shards = new IndexShards(D) { RoundRobinAdds = false, Threaded = true };
        for (int s = 0; s < 4; s++) shards.AddShard(new IndexFlatL2(D));
        shards.Add(data);

        var queries = TestData.Slice(data, D, 15);
        var sel = IDSelector.Range(250, 650); // spans shards 0, 1 and 2; shard 3 is skipped whole
        Assert.Equal(reference.Search(queries, K, sel).Labels, shards.Search(queries, K, sel).Labels);
        Assert.Equal(reference.Search(queries, K).Labels, shards.Search(queries, K).Labels);
    }

    [Fact]
    public void PreTransformForwardsParameters()
    {
        var data = TestData.Clustered(1000, D);
        var index = FaissNet.IndexFactory(D, "PCA16,Flat");
        index.Train(data);
        index.Add(data);
        var sel = IDSelector.Range(0, 100);
        AssertAllMembers(index.Search(TestData.Slice(data, D, 5), K, sel), sel);
    }

    [Fact]
    public void PerCallNprobeOverridesWithoutMutatingTheIndex()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, 30);
        var index = new IndexIVFFlat(D, nlist: 32) { Nprobe = 1 };
        index.Train(data);
        index.Add(data);

        var perCall = index.Search(queries, K, new SearchParametersIVF { Nprobe = 32 });
        Assert.Equal(1, index.Nprobe);
        index.Nprobe = 32;
        Assert.Equal(index.Search(queries, K).Labels, perCall.Labels);
    }

    [Fact]
    public void MaxCodesBoundsTheScan()
    {
        var data = TestData.Clustered(N, D);
        var index = new IndexIVFFlat(D, nlist: 16) { Nprobe = 16 };
        index.Train(data);
        index.Add(data);
        var queries = TestData.Slice(data, D, 10);
        var full = index.Search(queries, K);
        var bounded = index.Search(queries, K, new SearchParametersIVF { MaxCodes = 1 });
        // Stops after the first (nearest) list, which still holds each query's own vector.
        for (int q = 0; q < 10; q++) Assert.Equal(full.LabelsFor(q)[0], bounded.LabelsFor(q)[0]);
    }

    [Fact]
    public void RangeSearchHonoursTheSelector()
    {
        var data = TestData.Clustered(N, D);
        var flat = new IndexFlatL2(D);
        flat.Add(data);
        var ivf = new IndexIVFFlat(D, nlist: 8) { Nprobe = 8 };
        ivf.Train(data);
        ivf.Add(data);
        var queries = TestData.Slice(data, D, 5);
        var sel = IDSelector.Range(0, 1000);

        var fromFlat = flat.RangeSearch(queries, 0.5f, sel);
        var fromIvf = ivf.RangeSearch(queries, 0.5f, sel);
        Assert.All(fromFlat.Labels, id => Assert.InRange(id, 0, 999));
        Assert.True(fromFlat.TotalResults > 0);
        for (int q = 0; q < 5; q++)
            Assert.Equal(fromFlat.LabelsFor(q).ToArray().Order(), fromIvf.LabelsFor(q).ToArray().Order());
    }

    [Fact]
    public void SelectorAlgebra()
    {
        var range = IDSelector.Range(10, 20);
        var batch = IDSelector.Batch([5, 15, 25]);
        var bitmap = IDSelectorBitmap.FromIds([1, 9, 63], 64);

        Assert.True(range.IsMember(10) && !range.IsMember(20));
        Assert.True(IDSelector.And(range, batch).IsMember(15));
        Assert.False(IDSelector.And(range, batch).IsMember(5));
        Assert.True(IDSelector.Or(range, batch).IsMember(25));
        Assert.True(IDSelector.Not(range).IsMember(9));
        Assert.True(IDSelector.XOr(range, batch).IsMember(5) && !IDSelector.XOr(range, batch).IsMember(15));
        Assert.True(bitmap.IsMember(63) && bitmap.IsMember(1) && !bitmap.IsMember(2) && !bitmap.IsMember(64));
        Assert.False(range.MayIntersect(20, 30));
        Assert.True(range.MayIntersect(0, 10));
        Assert.False(batch.MayIntersect(26, 100));
    }

    [Fact]
    public void IndexesWithoutParameterSupportRejectThemLoudly()
    {
        using var gpu = new Gpu.IndexFlatL2Gpu(D);
        gpu.Add(TestData.Clustered(100, D));
        // Default parameters are fine everywhere; only a real filter must be refused.
        gpu.Search(TestData.Slice(TestData.Clustered(100, D), D, 1), K, new SearchParameters());
        Assert.Throws<NotSupportedException>(() =>
            gpu.Search(TestData.Slice(TestData.Clustered(100, D), D, 1), K, IDSelector.Range(0, 10)));
    }
}
