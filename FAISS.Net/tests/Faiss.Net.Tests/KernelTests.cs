using Faiss.Net.Core;
using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>
/// The v1.2 kernels: each is an optimization of something that already worked, so each is tested
/// against the path it replaces rather than against a recall threshold.
/// </summary>
public class KernelTests
{
    [Theory]
    [InlineData(MetricType.L2, 64, 3000, 37, 10)]
    [InlineData(MetricType.L2, 128, 1000, 100, 1)]
    [InlineData(MetricType.InnerProduct, 48, 2500, 21, 7)]
    [InlineData(MetricType.L2, 32, 300, 16, 400)] // k larger than the database: padding with -1
    public void BlockedProductAgreesWithThePairwiseKernel(MetricType metric, int d, int n, int nq, int k)
    {
        var all = FaissNet.RandomClusteredVectors(n + nq, d, 20, 0.1f, 11);
        var data = all.AsSpan(0, n * d).ToArray();
        var queries = all.AsSpan(n * d).ToArray();
        var index = new IndexFlat(d, metric);
        index.Add(data);

        var blocked = index.Search(queries, k);
        var pairwise = DirectSearch(data, queries, d, k, metric);

        int mismatches = 0;
        for (int i = 0; i < blocked.Labels.Length; i++)
        {
            if (blocked.Labels[i] != pairwise.Labels[i]) mismatches++;
            if (pairwise.Labels[i] < 0)
            {
                Assert.Equal(-1, blocked.Labels[i]);
                continue;
            }
            float expected = pairwise.Distances[i];
            Assert.True(Math.Abs(blocked.Distances[i] - expected) <= 1e-3f * Math.Max(1f, Math.Abs(expected)),
                $"distance {i}: blocked {blocked.Distances[i]} vs pairwise {expected}");
        }
        // Reordering is only allowed between near-ties, which on continuous data is very rare.
        Assert.True(mismatches <= blocked.Labels.Length / 200, $"{mismatches} of {blocked.Labels.Length} labels differ");
    }

    /// <summary>
    /// Reference k-NN by the scalar-exact pairwise kernel and a full sort — no index, no blocked
    /// product, no global switch that concurrently running tests could observe.
    /// </summary>
    private static SearchResult DirectSearch(float[] data, float[] queries, int d, int k, MetricType metric)
    {
        int n = data.Length / d, nq = queries.Length / d;
        var result = SearchResult.Allocate(nq, k);
        bool similarity = metric.IsSimilarity();
        for (int q = 0; q < nq; q++)
        {
            var scored = Enumerable.Range(0, n)
                .Select(i => (Id: (long)i, Score: VectorOps.Distance(queries.AsSpan(q * d, d), data.AsSpan(i * d, d), metric)))
                .OrderBy(x => similarity ? -x.Score : x.Score).ThenBy(x => x.Id).Take(k).ToArray();
            for (int r = 0; r < k; r++)
            {
                result.Labels[q * k + r] = r < scored.Length ? scored[r].Id : -1;
                result.Distances[q * k + r] = r < scored.Length ? scored[r].Score : (similarity ? float.MinValue : float.MaxValue);
            }
        }
        return result;
    }

    [Fact]
    public void BlockedProductFindsEveryVectorAsItsOwnNearestNeighbour()
    {
        const int d = 32, n = 2000;
        var data = TestData.Uniform(n, d);
        var index = new IndexFlatL2(d);
        index.Add(data);
        var result = index.Search(data, 1);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i, result.Labels[i]);
            Assert.True(result.Distances[i] >= 0f, "the norm decomposition must never report a negative distance");
        }
    }

    [Fact]
    public void Fp16DecodeIsBitExactForEveryHalf()
    {
        const int d = 8;
        var sq = new ScalarQuantizer(d, ScalarQuantizerType.Float16);
        var code = new byte[2 * d];
        var decoded = new float[d];

        for (int start = 0; start < 65536; start += d)
        {
            for (int j = 0; j < d; j++)
            {
                ushort bits = (ushort)(start + j);
                code[2 * j] = (byte)bits;
                code[2 * j + 1] = (byte)(bits >> 8);
            }
            sq.Decode(code, decoded);
            for (int j = 0; j < d; j++)
            {
                float expected = (float)BitConverter.UInt16BitsToHalf((ushort)(start + j));
                if (float.IsNaN(expected)) Assert.True(float.IsNaN(decoded[j]), $"0x{start + j:X4} should decode to NaN");
                else Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(decoded[j]));
            }
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(37)] // odd tail after the vector loop
    [InlineData(128)]
    public void FourBitDecodeMatchesTheScalarDefinition(int d)
    {
        var data = TestData.Uniform(400, d);
        var sq = new ScalarQuantizer(d, ScalarQuantizerType.PerDimension4Bit);
        sq.Train(data);
        var code = new byte[sq.CodeSize];
        var decoded = new float[d];
        var random = new Random(5);

        for (int trial = 0; trial < 200; trial++)
        {
            random.NextBytes(code);
            if (d % 2 == 1) code[^1] &= 0x0F; // the unused high nibble of an odd dimension
            sq.Decode(code, decoded);
            for (int j = 0; j < d; j++)
            {
                int q = (j & 1) == 0 ? code[j >> 1] & 0x0F : code[j >> 1] >> 4;
                Assert.Equal(sq.Offsets[j] + q * sq.Steps[j], decoded[j], 5);
            }
        }
    }

    [Fact]
    public void PrecomputedIvfpqTablesMatchTheDirectTables()
    {
        const int d = 32;
        var all = FaissNet.RandomClusteredVectors(5100, d, 30, 0.1f, 4);
        var data = all.AsSpan(0, 5000 * d).ToArray();
        var queries = all.AsSpan(5000 * d).ToArray();

        var index = new IndexIVFPQ(d, nlist: 32, m: 8) { Nprobe = 8 };
        index.Train(data);
        index.Add(data);
        var direct = index.Search(queries, 10);

        index.UsePrecomputedTable = true;
        var precomputed = index.Search(queries, 10);
        Assert.Equal((long)32 * 8 * 256 * sizeof(float), index.PrecomputedTableBytes);

        int mismatches = 0;
        for (int i = 0; i < direct.Labels.Length; i++)
        {
            if (direct.Labels[i] != precomputed.Labels[i]) mismatches++;
            Assert.Equal(direct.Distances[i], precomputed.Distances[i], 2);
        }
        Assert.True(mismatches <= direct.Labels.Length / 100, $"{mismatches} labels differ");
    }

    [Fact]
    public void PrecomputedTablesAreRebuiltAfterReload()
    {
        const int d = 16;
        var data = TestData.Clustered(3000, d);
        var index = new IndexIVFPQ(d, nlist: 16, m: 4) { Nprobe = 4, UsePrecomputedTable = true };
        index.Train(data);
        index.Add(data);
        var queries = TestData.Slice(data, d, 20);
        var before = index.Search(queries, 5);

        var reloaded = (IndexIVFPQ)IndexIO.Deserialize(IndexIO.Serialize(index));
        reloaded.UsePrecomputedTable = true; // a runtime choice, not part of the file
        Assert.Equal(before.Labels, reloaded.Search(queries, 5).Labels);
    }

    [Fact]
    public void ReducingOpqProjectsAndStillSearches()
    {
        const int d = 64;
        var all = FaissNet.RandomClusteredVectors(4100, d, 40, 0.1f, 8);
        var data = all.AsSpan(0, 4000 * d).ToArray();
        var queries = all.AsSpan(4000 * d).ToArray();
        var truth = TestData.GroundTruth(data, queries, d, 10);

        var opq = FaissNet.IndexFactory(d, "OPQ8_32,PQ8");
        opq.Train(data);
        opq.Add(data);
        var transform = (OPQMatrix)((IndexPreTransform)opq).Transforms[0];
        Assert.Equal(32, transform.DOut);
        Assert.True(transform.ErrorHistory[^1] <= transform.ErrorHistory[0], "OPQ iterations should not increase the error");

        double recall = FaissNet.ComputeRecall(truth, opq.Search(queries, 10));
        var pca = FaissNet.IndexFactory(d, "PCA32,PQ8");
        pca.Train(data);
        pca.Add(data);
        double pcaRecall = FaissNet.ComputeRecall(truth, pca.Search(queries, 10));
        Assert.True(recall >= pcaRecall - 0.05, $"reducing OPQ {recall:P1} vs PCA {pcaRecall:P1}");

        var reloaded = IndexIO.Deserialize(IndexIO.Serialize(opq));
        Assert.Equal(opq.Search(queries, 10).Labels, reloaded.Search(queries, 10).Labels);
    }

    [Theory]
    [InlineData("OPQ8,PQ8")]
    [InlineData("OPQ8,IVF16,PQ8")]
    [InlineData("OPQ8_32,IVF16,PQ8")]
    public void IndexesBehindAnOpqTransformRoundTrip(string description)
    {
        // Regression: before 1.1 the OPQ reader left M unread, misaligning everything after it.
        const int d = 64;
        var data = TestData.Clustered(3000, d);
        var index = FaissNet.IndexFactory(d, description);
        index.Train(data);
        index.Add(data);
        var reloaded = IndexIO.Deserialize(IndexIO.Serialize(index));
        var queries = TestData.Slice(data, d, 20);
        Assert.Equal(index.Search(queries, 10).Labels, reloaded.Search(queries, 10).Labels);
        Assert.Equal(8, ((OPQMatrix)((IndexPreTransform)reloaded).Transforms[0]).M);
    }
}

/// <summary>Mini-batch k-means: an approximation of Lloyd's that must stay close to it.</summary>
public class MiniBatchKmeansTests
{
    [Fact]
    public void MiniBatchObjectiveStaysCloseToLloyd()
    {
        const int d = 16, k = 32;
        var data = FaissNet.RandomClusteredVectors(40_000, d, k, 0.1f, 17);

        var lloyd = new Kmeans(d, k, new ClusteringParameters { Iterations = 20, Seed = 3 });
        double full = lloyd.Train(data);
        var miniBatch = new Kmeans(d, k, new ClusteringParameters { MiniBatchSize = 2048, MiniBatchIterations = 60, Seed = 3 });
        double approximate = miniBatch.Train(data);

        Assert.True(approximate < full * 1.15, $"mini-batch objective {approximate:G4} vs Lloyd {full:G4}");
        Assert.Equal(60, miniBatch.ObjectiveHistory.Count);
    }

    [Fact]
    public void IvfTrainsWithMiniBatchClustering()
    {
        const int d = 16;
        var data = TestData.Clustered(5000, d);
        var index = new IndexIVFFlat(d, nlist: 32)
        {
            ClusteringParameters = new ClusteringParameters { MiniBatchSize = 1024, MiniBatchIterations = 40 },
            Nprobe = 4,
        };
        index.Train(data);
        index.Add(data);
        var queries = TestData.Slice(data, d, 50);
        double recall = FaissNet.ComputeRecall(TestData.GroundTruth(data, queries, d, 10), index.Search(queries, 10));
        Assert.True(recall > 0.9, $"recall@10 with mini-batch-trained centroids was {recall:P1}");
    }
}

/// <summary>
/// Seeded training must be reproducible bit for bit. Regression: k-means and OPQ merged per-thread
/// partial sums in completion order, so the same seed trained slightly different codebooks.
/// </summary>
public class ReproducibilityTests
{
    [Theory]
    [InlineData("IVF64,PQ8")]
    [InlineData("OPQ8,IVF32,PQ8")]
    [InlineData("IVF64,SQ8")]
    public void SameSeedTrainsByteIdenticalIndexes(string description)
    {
        const int d = 32;
        var data = TestData.Clustered(6000, d);
        byte[] Build()
        {
            var index = FaissNet.IndexFactory(d, description);
            index.Train(data);
            index.Add(data);
            return Faiss.Net.IO.IndexIO.Serialize(index);
        }
        Assert.Equal(Build(), Build());
    }
}
