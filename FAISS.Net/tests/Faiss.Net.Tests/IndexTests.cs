using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Exactness, ordering and removal semantics of the flat indexes.</summary>
public class IndexFlatTests
{
    [Fact]
    public void SearchFindsEachVectorAsItsOwnNearestNeighbor()
    {
        const int d = 32, n = 500;
        var data = TestData.Uniform(n, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        var result = index.Search(data, k: 1);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i, result[i, 0].Id);
            Assert.Equal(0f, result[i, 0].Distance, 3);
        }
    }

    [Fact]
    public void DistancesAreOrderedBestFirst()
    {
        const int d = 16;
        var data = TestData.Uniform(200, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        var result = index.Search(TestData.Slice(data, d, 5), k: 10);
        for (int q = 0; q < 5; q++)
        {
            var distances = result.DistancesFor(q);
            for (int i = 1; i < distances.Length; i++)
                Assert.True(distances[i] >= distances[i - 1], "L2 distances must be non-decreasing.");
        }
    }

    [Fact]
    public void InnerProductOrdersByDecreasingSimilarity()
    {
        const int d = 16;
        var data = TestData.Uniform(200, d);
        var index = new IndexFlatIP(d);
        index.Add(data);

        var result = index.Search(TestData.Slice(data, d, 5), k: 10);
        for (int q = 0; q < 5; q++)
        {
            var scores = result.DistancesFor(q);
            for (int i = 1; i < scores.Length; i++)
                Assert.True(scores[i] <= scores[i - 1], "Inner-product scores must be non-increasing.");
        }
    }

    [Fact]
    public void SearchMatchesBruteForceReference()
    {
        const int d = 24, n = 300;
        var data = TestData.Uniform(n, d, seed: 7);
        var query = TestData.Uniform(1, d, seed: 99);

        var index = new IndexFlatL2(d);
        index.Add(data);
        var result = index.Search(query, k: 5);

        var reference = new List<(int Id, float Distance)>();
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int j = 0; j < d; j++)
            {
                float diff = query[j] - data[i * d + j];
                sum += diff * diff;
            }
            reference.Add((i, sum));
        }
        reference.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        for (int i = 0; i < 5; i++) Assert.Equal(reference[i].Id, result[0, i].Id);
    }

    [Fact]
    public void SearchWithMoreNeighborsThanVectorsPadsWithMinusOne()
    {
        var index = new IndexFlatL2(4);
        index.Add(new float[] { 1, 0, 0, 0, 0, 1, 0, 0 });

        var result = index.Search(new float[] { 1, 0, 0, 0 }, k: 5);
        Assert.Equal(0, result[0, 0].Id);
        Assert.Equal(1, result[0, 1].Id);
        // The shape stays nq x k, as in FAISS; the surplus columns are padded with -1.
        Assert.Equal(5, result.K);
        Assert.Equal([-1L, -1L, -1L], result.LabelsFor(0)[2..].ToArray());
    }

    [Fact]
    public void RowsStayAlignedWhenKExceedsNtotal()
    {
        // Regression guard: an index that shrank k internally would write rows at the wrong stride,
        // silently corrupting every query after the first.
        const int d = 4;
        var index = new IndexFlatL2(d);
        index.Add(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 });

        var queries = new float[] { 1, 0, 0, 0, 0, 1, 0, 0 };
        var result = index.Search(queries, k: 6);

        Assert.Equal(6, result.K);
        Assert.Equal(0, result[0, 0].Id);
        Assert.Equal(1, result[1, 0].Id);
        foreach (int q in new[] { 0, 1 })
            Assert.Equal([-1L, -1L, -1L], result.LabelsFor(q)[3..].ToArray());
    }

    [Fact]
    public void EmptyIndexReturnsNoNeighbors()
    {
        var index = new IndexFlatL2(8);
        var result = index.Search(new float[8], k: 3);
        Assert.All(result.Labels, label => Assert.Equal(-1, label));
    }

    [Fact]
    public void RemoveIdsCompactsAndRenumbers()
    {
        const int d = 8;
        var data = TestData.Uniform(10, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        long removed = index.RemoveIds([0L, 1L, 2L]);
        Assert.Equal(3, removed);
        Assert.Equal(7, index.Ntotal);

        // Position 0 now holds what was position 3.
        var reconstructed = index.Reconstruct(0);
        for (int j = 0; j < d; j++) Assert.Equal(data[3 * d + j], reconstructed[j], 5);
    }

    [Fact]
    public void RemoveByPredicateDropsMatchingIds()
    {
        var index = new IndexFlatL2(4);
        index.Add(TestData.Uniform(20, 4));

        long removed = index.RemoveIds(id => id % 2 == 0);
        Assert.Equal(10, removed);
        Assert.Equal(10, index.Ntotal);
    }

    [Fact]
    public void RangeSearchReturnsEverythingInsideTheRadius()
    {
        const int d = 8;
        var data = TestData.Uniform(400, d, seed: 11);
        var index = new IndexFlatL2(d);
        index.Add(data);

        var query = TestData.Slice(data, d, 1);
        var result = index.RangeSearch(query, radius: 0.5f);

        Assert.True(result.TotalResults > 0);
        foreach (var (_, distance) in result.Matches(0)) Assert.True(distance < 0.5f);
    }

    [Fact]
    public void ResetClearsVectorsButKeepsTheIndexUsable()
    {
        var index = new IndexFlatL2(8);
        index.Add(TestData.Uniform(50, 8));
        index.Reset();
        Assert.Equal(0, index.Ntotal);

        index.Add(TestData.Uniform(10, 8));
        Assert.Equal(10, index.Ntotal);
    }

    [Fact]
    public void JaggedAndFlatInputsAgree()
    {
        const int d = 6;
        var flat = TestData.Uniform(20, d);
        var jagged = new float[20][];
        for (int i = 0; i < 20; i++) jagged[i] = flat.AsSpan(i * d, d).ToArray();

        var a = new IndexFlatL2(d);
        a.Add(flat);
        var b = new IndexFlatL2(d);
        b.Add(jagged);

        var ra = a.Search(jagged[0], 3);
        var rb = b.Search(flat.AsSpan(0, d).ToArray(), 3);
        Assert.Equal(ra.Labels, rb.Labels);
    }

    [Fact]
    public void SingleQueryAndBatchQueryAgree()
    {
        const int d = 32;
        var data = TestData.Clustered(2000, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        var batch = index.Search(TestData.Slice(data, d, 20), k: 10);
        for (int q = 0; q < 20; q++)
        {
            var single = index.Search(data.AsSpan(q * d, d).ToArray(), k: 10);
            Assert.Equal(batch.LabelsFor(q).ToArray(), single.LabelsFor(0).ToArray());
        }
    }
}

/// <summary>Recall and behaviour of the approximate indexes, plus the exact ones they wrap.</summary>
public class ApproximateIndexTests
{
    private const int D = 48;
    private const int N = 4000;
    private const int Queries = 100;

    [Fact]
    public void IvfFlatIsExactWhenEveryCellIsProbed()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexIVFFlat(D, nlist: 32);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 32; // every cell -> an exhaustive scan

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.Equal(1.0, recall, 3);
    }

    [Fact]
    public void IvfFlatRecallImprovesWithNprobe()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexIVFFlat(D, nlist: 64);
        index.Train(data);
        index.Add(data);

        index.Nprobe = 1;
        double low = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        index.Nprobe = 16;
        double high = FaissNet.ComputeRecall(truth, index.Search(queries, 10));

        Assert.True(high >= low, $"recall should not fall as nprobe rises: {low:P1} -> {high:P1}");
        Assert.True(high > 0.9, $"nprobe=16 of 64 should recover most neighbours, got {high:P1}");
    }

    [Fact]
    public void IvfPqRecoversMostNeighborsAtHeavyCompression()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexIVFPQ(D, nlist: 32, m: 12);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 16;

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.55, $"IVFPQ recall@10 was {recall:P1}");
        Assert.True(index.CompressionRatio > 10, "12 bytes should be far smaller than 48 floats.");
    }

    [Fact]
    public void PqAloneScansEveryVectorAndKeepsTheTopHit()
    {
        var data = TestData.Clustered(2000, D);
        var queries = TestData.Slice(data, D, 50);

        var index = new IndexPQ(D, m: 12);
        index.Train(data);
        index.Add(data);

        // Every vector is compared, so the query's own code must come back first.
        var result = index.Search(queries, k: 1);
        int exact = 0;
        for (int q = 0; q < 50; q++) if (result[q, 0].Id == q) exact++;
        Assert.True(exact >= 45, $"only {exact}/50 queries returned themselves");
    }

    [Fact]
    public void ScalarQuantizerKeepsNearlyAllRecall()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexScalarQuantizer(D, ScalarQuantizerType.PerDimension8Bit);
        index.Train(data);
        index.Add(data);

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.95, $"8-bit SQ recall@10 was {recall:P1}");
        Assert.Equal(4.0, index.CompressionRatio, 1);
    }

    [Fact]
    public void Float16ScalarQuantizerIsNearlyLossless()
    {
        var data = TestData.Clustered(2000, D);
        var queries = TestData.Slice(data, D, 50);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexScalarQuantizer(D, ScalarQuantizerType.Float16);
        index.Add(data);

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.99, $"fp16 recall@10 was {recall:P1}");
    }

    [Fact]
    public void HnswReachesHighRecall()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexHNSWFlat(D, m: 16) { EfConstruction = 80, EfSearch = 64 };
        index.Add(data);

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.9, $"HNSW recall@10 was {recall:P1}");
    }

    [Fact]
    public void HnswRecallImprovesWithEfSearch()
    {
        var data = TestData.Clustered(N, D);
        var queries = TestData.Slice(data, D, Queries);
        var truth = TestData.GroundTruth(data, queries, D, 10);

        var index = new IndexHNSWFlat(D, m: 8) { EfConstruction = 40 };
        index.Add(data);

        index.EfSearch = 10;
        double low = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        index.EfSearch = 200;
        double high = FaissNet.ComputeRecall(truth, index.Search(queries, 10));

        Assert.True(high >= low, $"recall should not fall as efSearch rises: {low:P1} -> {high:P1}");
    }

    [Fact]
    public void HnswGraphIsConnectedEnoughToBeNavigable()
    {
        var data = TestData.Clustered(2000, D);
        var index = new IndexHNSWFlat(D, m: 16);
        index.Add(data);

        Assert.True(index.Graph.AverageDegree() > 8,
            $"average layer-0 degree was {index.Graph.AverageDegree():F1}, too sparse to navigate");
        Assert.True(index.Graph.MaxLevel >= 1, "a 2000-node graph should have more than one layer");
    }

    [Fact]
    public void InnerProductIvfWorksWithNormalizedVectors()
    {
        var data = TestData.Clustered(2000, D);
        FaissNet.NormalizeL2(data, D);
        var queries = TestData.Slice(data, D, 50);

        var index = new IndexIVFFlat(D, nlist: 16, MetricType.InnerProduct);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 16;

        var result = index.Search(queries, k: 1);
        // A unit vector's best cosine match is itself, at similarity 1.
        for (int q = 0; q < 50; q++) Assert.Equal(1f, result[q, 0].Distance, 2);
    }
}
