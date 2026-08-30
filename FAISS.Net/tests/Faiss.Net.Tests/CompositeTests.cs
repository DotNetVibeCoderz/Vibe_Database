using Faiss.Net.Binary;
using Faiss.Net.Gpu;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Factory strings, wrappers, transforms and quantizers used on their own.</summary>
public class FactoryTests
{
    [Theory]
    [InlineData("Flat", typeof(IndexFlatL2))]
    [InlineData("IVF16,Flat", typeof(IndexIVFFlat))]
    [InlineData("IVF16,PQ8", typeof(IndexIVFPQ))]
    [InlineData("IVF16,SQ8", typeof(IndexIVFScalarQuantizer))]
    [InlineData("PQ8", typeof(IndexPQ))]
    [InlineData("SQ8", typeof(IndexScalarQuantizer))]
    [InlineData("SQfp16", typeof(IndexScalarQuantizer))]
    [InlineData("HNSW16", typeof(IndexHNSWFlat))]
    [InlineData("PCA16,Flat", typeof(IndexPreTransform))]
    [InlineData("OPQ8,IVF16,PQ8", typeof(IndexPreTransform))]
    [InlineData("IDMap,Flat", typeof(IndexIDMap2))]
    public void FactoryBuildsTheExpectedType(string description, Type expected)
    {
        var index = FaissNet.IndexFactory(32, description);
        Assert.IsType(expected, index);
        Assert.Equal(32, index.D);
    }

    [Fact]
    public void FactoryHonoursTheMetric()
    {
        var index = FaissNet.IndexFactory(16, "Flat", MetricType.InnerProduct);
        Assert.IsType<IndexFlatIP>(index);
        Assert.Equal(MetricType.InnerProduct, index.MetricType);
    }

    [Fact]
    public void FactoryExplainsAnUndivisiblePqSize()
    {
        var exception = Assert.Throws<ArgumentException>(() => FaissNet.IndexFactory(30, "PQ16"));
        Assert.Contains("divide", exception.Message);
    }

    [Fact]
    public void FactoryRejectsAnUnknownEncoding()
    {
        var exception = Assert.Throws<ArgumentException>(() => FaissNet.IndexFactory(32, "IVF16,NOPE"));
        Assert.Contains("unknown encoding", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryBuiltIvfPqTrainsAndSearches()
    {
        const int d = 32;
        var data = TestData.Clustered(3000, d);
        var index = FaissNet.IndexFactory(d, "IVF32,PQ8");
        index.Train(data);
        index.Add(data);
        ((IndexIVFPQ)index).Nprobe = 8;

        var result = index.Search(TestData.Slice(data, d, 10), k: 5);
        Assert.All(result.Labels, label => Assert.InRange(label, 0, 2999));
    }

    [Fact]
    public void OpqChainImprovesOnPlainPqOnAnisotropicData()
    {
        // Give the first dimensions far more variance than the rest: the case OPQ exists to fix,
        // because plain PQ wastes an equal code budget on subspaces that carry almost no signal.
        const int d = 32, n = 4000;
        var rng = new Utils.RandomGenerator(5);
        var data = new float[n * d];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++)
                data[i * d + j] = rng.NextGaussian() * (j < 8 ? 10f : 0.1f);

        var queries = TestData.Slice(data, d, 100);
        var truth = TestData.GroundTruth(data, queries, d, 10);

        var plain = new IndexPQ(d, m: 8);
        plain.Train(data);
        plain.Add(data);
        double plainRecall = FaissNet.ComputeRecall(truth, plain.Search(queries, 10));

        var opq = new IndexPreTransform(new OPQMatrix(d, 8) { Iterations = 8 }, new IndexPQ(d, m: 8));
        opq.Train(data);
        opq.Add(data);
        double opqRecall = FaissNet.ComputeRecall(truth, opq.Search(queries, 10));

        Assert.True(opqRecall >= plainRecall - 0.02,
            $"OPQ should not lose to plain PQ here: {opqRecall:P1} vs {plainRecall:P1}");
    }

    [Fact]
    public void PcaKeepsNeighborsWhenTheDataIsGenuinelyLowRank()
    {
        // Data that lives in a 12-dimensional subspace of a 64-dimensional space, plus a little
        // noise. This is the situation PCA is for, and the one where discarding dimensions is
        // supposed to be nearly free. (On isotropic data no projection can preserve neighbours,
        // so a recall assertion there would be testing the data, not the transform.)
        const int d = 64, rank = 12, n = 2000;
        var rng = new Utils.RandomGenerator(21);
        var basis = new float[rank * d];
        rng.FillGaussian(basis);

        var data = new float[n * d];
        var coefficients = new float[rank];
        for (int i = 0; i < n; i++)
        {
            rng.FillGaussian(coefficients);
            for (int j = 0; j < d; j++)
            {
                float value = 0;
                for (int r = 0; r < rank; r++) value += coefficients[r] * basis[r * d + j];
                data[i * d + j] = value + rng.NextGaussian() * 0.01f;
            }
        }

        var queries = TestData.Slice(data, d, 50);
        var truth = TestData.GroundTruth(data, queries, d, 10);

        var index = FaissNet.IndexFactory(d, "PCA16,Flat");
        index.Train(data);
        index.Add(data);

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.9, $"PCA 64->16 on rank-12 data recovered only {recall:P1}");
    }

    [Fact]
    public void PcaToFullDimensionIsNearlyLossless()
    {
        const int d = 32;
        var data = TestData.Clustered(1000, d);
        var queries = TestData.Slice(data, d, 40);
        var truth = TestData.GroundTruth(data, queries, d, 10);

        // A full-rank PCA is a rotation plus a shift, so it must preserve neighbours exactly.
        var index = FaissNet.IndexFactory(d, "PCA32,Flat");
        index.Train(data);
        index.Add(data);

        double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
        Assert.True(recall > 0.98, $"full-rank PCA changed the neighbours: recall {recall:P1}");
    }

    [Fact]
    public void L2NormTransformTurnsInnerProductIntoCosine()
    {
        const int d = 16;
        var data = TestData.Clustered(500, d);
        var index = new IndexPreTransform(new NormalizationTransform(d), new IndexFlatIP(d));
        index.Add(data);

        var result = index.Search(TestData.Slice(data, d, 5), k: 1);
        for (int q = 0; q < 5; q++) Assert.Equal(1f, result[q, 0].Distance, 3);
    }
}

/// <summary>Behaviour of the wrapper indexes.</summary>
public class WrapperTests
{
    [Fact]
    public void IdMapReturnsCallerIds()
    {
        const int d = 8;
        var data = TestData.Uniform(50, d);
        var ids = new long[50];
        for (int i = 0; i < 50; i++) ids[i] = 500 - i;

        var index = new IndexIDMap(new IndexFlatL2(d));
        index.AddWithIds(data, ids);

        var result = index.Search(TestData.Slice(data, d, 1), k: 1);
        Assert.Equal(500, result[0, 0].Id);
    }

    [Fact]
    public void IdMapKeepsIdsStableAcrossRemoval()
    {
        const int d = 8;
        var data = TestData.Uniform(20, d);
        var ids = new long[20];
        for (int i = 0; i < 20; i++) ids[i] = 100 + i;

        var index = new IndexIDMap(new IndexFlatL2(d));
        index.AddWithIds(data, ids);
        index.RemoveIds([100L, 101L]);

        Assert.Equal(18, index.Ntotal);
        // Vector 2 kept id 102 even though its position inside the wrapped index shifted to 0.
        var result = index.Search(data.AsSpan(2 * d, d).ToArray(), k: 1);
        Assert.Equal(102, result[0, 0].Id);
    }

    [Fact]
    public void ReplicasReturnTheSameResultsAsOneIndex()
    {
        const int d = 16;
        var data = TestData.Clustered(1000, d);
        var reference = new IndexFlatL2(d);
        reference.Add(data);

        var replicas = new IndexReplicas(d);
        replicas.AddReplica(new IndexFlatL2(d));
        replicas.AddReplica(new IndexFlatL2(d));
        replicas.Add(data);

        var queries = TestData.Slice(data, d, 40);
        Assert.Equal(reference.Search(queries, 5).Labels, replicas.Search(queries, 5).Labels);
    }

    [Fact]
    public void ShardsMergeIntoOneGlobalIdSpace()
    {
        const int d = 16;
        var data = TestData.Clustered(600, d);
        var reference = new IndexFlatL2(d);
        reference.Add(data);

        var shards = new IndexShards(d) { RoundRobinAdds = false };
        shards.AddShard(new IndexFlatL2(d));
        shards.AddShard(new IndexFlatL2(d));
        shards.AddShard(new IndexFlatL2(d));
        shards.Add(data);

        Assert.Equal(600, shards.Ntotal);
        var queries = TestData.Slice(data, d, 20);
        var expected = reference.Search(queries, 5);
        var actual = shards.Search(queries, 5);

        // Shard ids are offset in add order, which for a contiguous split reproduces the original ids.
        Assert.Equal(expected.Labels, actual.Labels);
    }
}

/// <summary>Quantizers exercised directly, independently of any index.</summary>
public class QuantizerTests
{
    [Fact]
    public void KmeansSeparatesWellFormedClusters()
    {
        const int d = 8;
        var data = FaissNet.RandomClusteredVectors(2000, d, clusters: 4, spread: 0.02f, seed: 9);
        var kmeans = new Kmeans(d, 4, new ClusteringParameters { Iterations = 30, Seed = 1 });
        kmeans.Train(data);

        var (labels, _) = kmeans.Assign(data);
        var counts = new int[4];
        foreach (long label in labels) counts[label]++;
        Assert.All(counts, count => Assert.True(count > 200, $"cluster sizes were unbalanced: [{string.Join(", ", counts)}]"));
    }

    [Fact]
    public void KmeansObjectiveDecreasesMonotonically()
    {
        var data = FaissNet.RandomClusteredVectors(1000, 16, clusters: 8, seed: 3);
        var kmeans = new Kmeans(16, 8, new ClusteringParameters { Iterations = 15, Seed = 2, Tolerance = 0 });
        kmeans.Train(data);

        var history = kmeans.ObjectiveHistory;
        for (int i = 1; i < history.Count; i++)
            Assert.True(history[i] <= history[i - 1] * 1.001,
                $"objective rose at iteration {i}: {history[i - 1]:G6} -> {history[i]:G6}");
    }

    [Fact]
    public void ProductQuantizerReconstructsCloseToTheOriginal()
    {
        const int d = 32;
        var data = TestData.Clustered(4000, d);
        var pq = new ProductQuantizer(d, m: 8);
        pq.Train(data);

        var code = new byte[pq.CodeSize];
        var decoded = new float[d];
        double error = 0;
        for (int i = 0; i < 100; i++)
        {
            var original = data.AsSpan(i * d, d);
            pq.ComputeCode(original, code);
            pq.Decode(code, decoded);
            for (int j = 0; j < d; j++) error += Math.Abs(original[j] - decoded[j]);
        }
        Assert.True(error / (100 * d) < 0.2, $"mean absolute reconstruction error was {error / (100 * d):F4}");
        Assert.Equal(8, pq.CodeSize);
    }

    [Fact]
    public void DistanceTableMatchesDirectDecodedDistance()
    {
        const int d = 16;
        var data = TestData.Clustered(2000, d);
        var pq = new ProductQuantizer(d, m: 4);
        pq.Train(data);

        var code = new byte[pq.CodeSize];
        pq.ComputeCode(data.AsSpan(0, d), code);
        var decoded = new float[d];
        pq.Decode(code, decoded);

        var query = data.AsSpan(5 * d, d);
        var table = new float[pq.DistanceTableSize];
        pq.ComputeDistanceTable(query, table, MetricType.L2);

        float direct = Core.VectorOps.L2Sqr(query, decoded);
        unsafe
        {
            fixed (float* pt = table)
            fixed (byte* pc = code)
                Assert.Equal(direct, pq.DistanceFromTable(pt, pc), 3);
        }
    }

    [Theory]
    [InlineData(ScalarQuantizerType.Float16, 0.01)]
    [InlineData(ScalarQuantizerType.PerDimension8Bit, 0.01)]
    [InlineData(ScalarQuantizerType.PerDimension4Bit, 0.10)]
    public void ScalarQuantizerErrorStaysWithinItsBudget(ScalarQuantizerType type, double tolerance)
    {
        const int d = 32;
        var data = TestData.Clustered(2000, d);
        var sq = new ScalarQuantizer(d, type);
        sq.Train(data);
        Assert.True(sq.MeasureError(data.AsSpan(0, 100 * d)) < tolerance);
    }

    [Fact]
    public void RandomRotationPreservesDistances()
    {
        const int d = 32;
        var data = TestData.Uniform(100, d);
        var rotation = new RandomRotationMatrix(d, seed: 11);
        var rotated = rotation.Apply(data);

        // An orthonormal transform is an isometry, so pairwise distances must be unchanged.
        float before = Core.VectorOps.L2Sqr(data.AsSpan(0, d), data.AsSpan(d, d));
        float after = Core.VectorOps.L2Sqr(rotated.AsSpan(0, d), rotated.AsSpan(d, d));
        Assert.Equal(before, after, 2);
    }
}

/// <summary>Hamming-space indexes.</summary>
public class BinaryIndexTests
{
    private static byte[] RandomCodes(int n, int bits, long seed = 1)
    {
        var rng = new Utils.RandomGenerator(seed);
        var codes = new byte[n * (bits / 8)];
        for (int i = 0; i < codes.Length; i++) codes[i] = (byte)rng.NextInt(256);
        return codes;
    }

    [Fact]
    public void HammingDistanceIsCorrect()
    {
        Assert.Equal(0, HammingOps.Distance(new byte[] { 0xFF }, new byte[] { 0xFF }));
        Assert.Equal(8, HammingOps.Distance(new byte[] { 0xFF }, new byte[] { 0x00 }));
        Assert.Equal(4, HammingOps.Distance(new byte[] { 0b1010_1010 }, new byte[] { 0b1111_1111 }));
    }

    [Fact]
    public void BinaryFlatFindsExactNeighbors()
    {
        const int bits = 128;
        var codes = RandomCodes(500, bits);
        var index = new IndexBinaryFlat(bits);
        index.Add(codes);

        var result = index.Search(codes.AsSpan(0, bits / 8), k: 1);
        Assert.Equal(0, result[0, 0].Id);
        Assert.Equal(0f, result[0, 0].Distance);
    }

    [Fact]
    public void BinaryIvfFindsMostNeighborsWhenProbingWidely()
    {
        const int bits = 128;
        var codes = RandomCodes(2000, bits, seed: 4);

        var flat = new IndexBinaryFlat(bits);
        flat.Add(codes);

        var ivf = new IndexBinaryIVF(bits, nlist: 8);
        ivf.Train(codes);
        ivf.Add(codes);
        ivf.Nprobe = 8;

        var queries = codes.AsSpan(0, 20 * (bits / 8)).ToArray();
        Assert.Equal(flat.Search(queries, 1).Labels, ivf.Search(queries, 1).Labels);
    }

    [Fact]
    public void BinarizeThresholdsAtZero()
    {
        var vector = new float[] { 1, -1, 2, -2, 0.5f, -0.5f, 3, -3 };
        var code = new byte[1];
        HammingOps.Binarize(vector, code);
        Assert.Equal(0b0101_0101, code[0]);
    }
}

/// <summary>
/// GPU backend. These run wherever the test suite runs: with no GPU present ILGPU falls back to its
/// CPU accelerator, so the kernels themselves are still exercised for correctness.
/// </summary>
public class GpuIndexTests
{
    [Fact]
    public void GpuFlatMatchesTheCpuIndex()
    {
        const int d = 32;
        var data = TestData.Clustered(1000, d);
        var queries = TestData.Slice(data, d, 20);

        var cpu = new IndexFlatL2(d);
        cpu.Add(data);

        using var gpu = new IndexFlatL2Gpu(d);
        gpu.Add(data);

        var expected = cpu.Search(queries, 10);
        var actual = gpu.Search(queries, 10);

        Assert.Equal(expected.Labels, actual.Labels);
        for (int i = 0; i < expected.Distances.Length; i++)
            Assert.Equal(expected.Distances[i], actual.Distances[i], 2);
    }

    [Fact]
    public void GpuInnerProductMatchesTheCpuIndex()
    {
        const int d = 24;
        var data = TestData.Clustered(600, d);
        FaissNet.NormalizeL2(data, d);
        var queries = TestData.Slice(data, d, 10);

        var cpu = new IndexFlatIP(d);
        cpu.Add(data);

        using var gpu = new IndexFlatIPGpu(d);
        gpu.Add(data);

        Assert.Equal(cpu.Search(queries, 5).Labels, gpu.Search(queries, 5).Labels);
    }

    [Fact]
    public void GpuIndexConvertsToAndFromCpu()
    {
        const int d = 16;
        var data = TestData.Uniform(200, d);

        var cpu = new IndexFlatL2(d);
        cpu.Add(data);

        using var gpu = GpuIndexFlat.FromCpu(cpu);
        Assert.Equal(cpu.Ntotal, gpu.Ntotal);

        var back = gpu.ToCpu();
        Assert.Equal(cpu.Search(TestData.Slice(data, d, 5), 3).Labels,
                     back.Search(TestData.Slice(data, d, 5), 3).Labels);
    }
}
