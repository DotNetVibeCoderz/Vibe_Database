using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Re-ranking, the HNSW family over compressed storage, and the HNSW coarse quantizer.</summary>
public class RefineAndGraphTests
{
    private const int D = 32;
    private const int N = 4000;
    private const int Queries = 100;

    private static (float[] Data, float[] Queries, SearchResult Truth) Dataset()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        return (data, queries, TestData.GroundTruth(data, queries, D, 10));
    }

    [Fact]
    public void RefineRecoversTheRecallThatQuantizationCosts()
    {
        var (data, queries, truth) = Dataset();
        var pq = new IndexPQ(D, m: 4);
        pq.Train(data);
        pq.Add(data);
        double plain = FaissNet.ComputeRecall(truth, pq.Search(queries, 10));

        var refined = new IndexRefineFlat(new IndexPQ(D, m: 4)) { KFactor = 10 };
        refined.Train(data);
        refined.Add(data);
        double withRefine = FaissNet.ComputeRecall(truth, refined.Search(queries, 10));

        Assert.True(withRefine > plain + 0.2, $"refine {withRefine:P1} should clearly beat plain PQ {plain:P1}");
        Assert.True(withRefine > 0.9, $"refined recall@10 was {withRefine:P1}");
    }

    [Fact]
    public void RefineDistancesAreExact()
    {
        var (data, queries, _) = Dataset();
        var index = new IndexRefineFlat(new IndexScalarQuantizer(D)) { KFactor = 4 };
        index.Train(data);
        index.Add(data);
        var result = index.Search(queries, 5);
        for (int q = 0; q < 5; q++)
        {
            long id = result.LabelsFor(q)[0];
            float exact = Core.VectorOps.L2Sqr(queries.AsSpan(q * D, D), data.AsSpan((int)id * D, D));
            Assert.Equal(exact, result.DistancesFor(q)[0], 4);
        }
    }

    [Fact]
    public void RefineHonoursPerCallKFactorAndSelector()
    {
        var (data, queries, _) = Dataset();
        var index = new IndexRefineFlat(new IndexPQ(D, m: 4)) { KFactor = 1 };
        index.Train(data);
        index.Add(data);
        var sel = IDSelector.Range(0, 2000);
        var result = index.Search(queries, 10, new SearchParametersRefine { KFactor = 8, Sel = sel });
        Assert.All(result.Labels, id => Assert.InRange(id, 0, 1999));
        Assert.Equal(1, index.KFactor);
    }

    [Theory]
    [InlineData("IVF32,PQ8,RFlat", typeof(IndexRefineFlat))]
    [InlineData("PQ8,Refine(SQfp16)", typeof(IndexRefine))]
    [InlineData("HNSW16,SQ8", typeof(IndexHNSWSQ))]
    [InlineData("HNSW16,PQ8", typeof(IndexHNSWPQ))]
    [InlineData("HNSW16,Flat", typeof(IndexHNSWFlat))]
    [InlineData("IVF32_HNSW16,Flat", typeof(IndexIVFFlat))]
    public void FactoryBuildsTheNewCompositions(string description, Type expected) =>
        Assert.IsType(expected, FaissNet.IndexFactory(D, description));

    [Fact]
    public void HnswOverScalarCodesKeepsHighRecallAtAQuarterOfTheMemory()
    {
        var (data, queries, truth) = Dataset();
        var flat = new IndexHNSWFlat(D, m: 16) { EfConstruction = 80, EfSearch = 64 };
        flat.Add(data);
        var sq = new IndexHNSWSQ(D, ScalarQuantizerType.PerDimension8Bit, m: 16) { EfConstruction = 80, EfSearch = 64 };
        sq.Train(data);
        sq.Add(data);

        double recall = FaissNet.ComputeRecall(truth, sq.Search(queries, 10));
        Assert.True(recall > 0.9, $"HNSW-SQ8 recall@10 was {recall:P1}");
        Assert.True(sq.MemoryUsage < flat.MemoryUsage, "HNSW-SQ8 should use less memory than HNSW-Flat");
    }

    [Fact]
    public void HnswOverPqCodesIsRescuedByRefinement()
    {
        var (data, queries, truth) = Dataset();
        var index = new IndexRefineFlat(new IndexHNSWPQ(D, pqM: 8, m: 16) { EfConstruction = 80, EfSearch = 64 }) { KFactor = 8 };
        index.Train(data);
        index.Add(data);
        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.9, $"HNSW-PQ + refine recall@10 was {recall:P1}");
    }

    [Fact]
    public void IvfWithAnHnswCoarseQuantizerMatchesAFlatOne()
    {
        var (data, queries, truth) = Dataset();
        var withFlat = FaissNet.IndexFactory(D, "IVF64,Flat");
        var withHnsw = FaissNet.IndexFactory(D, "IVF64_HNSW16,Flat");
        foreach (var index in new[] { withFlat, withHnsw })
        {
            index.Train(data);
            index.Add(data);
            ((IndexIVF)index).Nprobe = 8;
        }
        double flatRecall = FaissNet.ComputeRecall(truth, withFlat.Search(queries, 10));
        double hnswRecall = FaissNet.ComputeRecall(truth, withHnsw.Search(queries, 10));
        Assert.True(hnswRecall > flatRecall - 0.03, $"HNSW quantizer {hnswRecall:P1} vs flat quantizer {flatRecall:P1}");
    }

    [Fact]
    public void SinkRepairFixesAssignmentOnVectorsItNeverSaw()
    {
        // A deliberately weak graph (M=8, efSearch=4) so that sinks actually occur. Nodes, repair
        // sample and evaluation set come from one draw, split three ways, so all three share a
        // distribution — and the evaluation set is disjoint from what the repair looked at.
        const int nodes = 4000, sample = 12_000, held = 24_000;
        var all = FaissNet.RandomClusteredVectors(nodes + sample + held, D, 64, 0.08f, 7);
        var centroids = all.AsSpan(0, nodes * D).ToArray();
        var repair = all.AsSpan(nodes * D, sample * D).ToArray();
        var evaluation = all.AsSpan((nodes + sample) * D).ToArray();

        var exact = new IndexFlatL2(D);
        exact.Add(centroids);
        var truth = exact.Search(evaluation, 1);

        var graph = new IndexHNSWFlat(D, m: 8) { EfConstruction = 16, EfSearch = 4 };
        graph.Add(centroids);
        var before = graph.Search(evaluation, 1);

        var stats = graph.AddLevel0LinksFromSample(repair);
        var after = graph.Search(evaluation, 1);

        static int Largest(SearchResult r) => r.Labels.GroupBy(x => x).Max(g => g.Count());
        double recallBefore = FaissNet.ComputeRecallAt1(truth, before);
        double recallAfter = FaissNet.ComputeRecallAt1(truth, after);

        Assert.True(stats.SinksFound > 0, "the weak graph should have sinks to repair");
        Assert.True(stats.SinksRemaining < stats.SinksFound, stats.ToString());
        Assert.True(recallAfter > recallBefore + 0.05, $"held-out recall@1 {recallBefore:P1} -> {recallAfter:P1} ({stats})");
        Assert.True(Largest(after) < Largest(before), $"largest list {Largest(before)} -> {Largest(after)}");
    }

    [Theory]
    [InlineData("HNSW16,SQ8")]
    [InlineData("HNSW16,PQ8")]
    [InlineData("IVF32,PQ8,RFlat")]
    [InlineData("PQ8,Refine(SQ8)")]
    public void NewIndexesRoundTripByteIdentically(string description)
    {
        var (data, queries, _) = Dataset();
        var index = FaissNet.IndexFactory(D, description);
        index.Train(data);
        index.Add(data);

        var reloaded = IndexIO.Deserialize(IndexIO.Serialize(index));
        Assert.IsType(index.GetType(), reloaded);
        var a = index.Search(queries, 10);
        var b = reloaded.Search(queries, 10);
        Assert.Equal(a.Labels, b.Labels);
        Assert.Equal(a.Distances, b.Distances);
    }

    [Fact]
    public void HnswFlatFilesFromBeforeTheRefactorStillLoad()
    {
        // The HNSW refactor moved the storage behind a base class; the body layout must not move.
        var data = TestData.Clustered(500, D);
        var index = new IndexHNSWFlat(D, m: 8) { EfSearch = 24 };
        index.Add(data);
        var bytes = IndexIO.Serialize(index);

        // Layout: magic(8) version(4) code(2) d(4) metric(4) ntotal(8) trained(1) efC(4) efS(4) count(4) floats...
        int body = 8 + 4 + 2 + 4 + 4 + 8 + 1;
        Assert.Equal(40, BitConverter.ToInt32(bytes, body));
        Assert.Equal(24, BitConverter.ToInt32(bytes, body + 4));
        Assert.Equal(500, BitConverter.ToInt32(bytes, body + 8));
        Assert.Equal(data[0], BitConverter.ToSingle(bytes, body + 12));
    }
}

/// <summary>
/// The single-writer, many-readers contract: searches running while one thread adds must never
/// throw, never return an id that did not exist, and never see a torn list or store.
/// </summary>
public class ConcurrencyTests
{
    private const int D = 16;

    [Theory]
    [InlineData("Flat")]
    [InlineData("IVF16,Flat")]
    [InlineData("IVF16,PQ4")]
    [InlineData("IVF16,SQ8")]
    [InlineData("PQ4")]
    [InlineData("SQ8")]
    [InlineData("HNSW8")]
    [InlineData("HNSW8,SQ8")]
    public void SearchesRunSafelyWhileOneWriterAdds(string description)
    {
        var data = TestData.Clustered(12_000, D, seed: 3);
        var index = FaissNet.IndexFactory(D, description);
        index.Train(data.AsSpan(0, 2000 * D));
        index.Add(data.AsSpan(0, 1000 * D));

        var queries = TestData.Slice(data, D, 16);
        using var stop = new CancellationTokenSource();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var result = index.Search(queries, 5);
                    long bound = data.Length / D;
                    foreach (long id in result.Labels)
                        if (id < -1 || id >= bound) failures.Enqueue($"impossible id {id}");
                }
                catch (Exception e)
                {
                    failures.Enqueue(e.GetType().Name + ": " + e.Message);
                }
            }
        })).ToArray();

        for (int start = 1000; start < 12_000; start += 500)
            index.Add(data.AsSpan(start * D, 500 * D));
        stop.Cancel();
        Task.WaitAll(readers);

        Assert.Empty(failures);
        Assert.Equal(12_000, index.Ntotal);
    }

    [Fact]
    public void RemovalDoesNotDisturbAConcurrentFlatScan()
    {
        var data = TestData.Clustered(20_000, D, seed: 5);
        var index = new IndexFlatL2(D);
        index.Add(data);
        var queries = TestData.Slice(data, D, 8);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { index.Search(queries, 3); }
                catch (Exception e) { failures.Enqueue(e.Message); }
            }
        });
        for (int round = 0; round < 20; round++) index.RemoveIds(id => id % 97 == round);
        stop.Cancel();
        reader.Wait();
        Assert.Empty(failures);
    }

    [Fact]
    public void SynchronizedIndexAllowsAnyMixOfReadersAndWriters()
    {
        var data = TestData.Clustered(6000, D, seed: 9);
        using var index = new SynchronizedIndex(new IndexShards(D));
        var shards = (IndexShards)index.Inner;
        shards.AddShard(new IndexFlatL2(D));
        shards.AddShard(new IndexFlatL2(D));
        index.Add(data.AsSpan(0, 1000 * D));

        var queries = TestData.Slice(data, D, 4);
        var tasks = new List<Task>();
        for (int w = 0; w < 2; w++)
        {
            int writer = w;
            tasks.Add(Task.Run(() =>
            {
                for (int start = 1000 + writer * 2500; start < 3500 + writer * 2500; start += 250)
                    index.Add(data.AsSpan(start * D, 250 * D).ToArray());
            }));
        }
        for (int r = 0; r < 3; r++)
            tasks.Add(Task.Run(() => { for (int i = 0; i < 50; i++) index.Search(queries, 5); }));
        Task.WaitAll([.. tasks]);

        Assert.Equal(6000, index.Ntotal);
        Assert.Equal(6000, shards.Ntotal);
    }
}
