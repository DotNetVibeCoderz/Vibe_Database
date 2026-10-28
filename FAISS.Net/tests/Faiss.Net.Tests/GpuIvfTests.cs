using Faiss.Net.Gpu;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>
/// GPU inverted-file indexes. Each GPU index wraps a CPU index for training and encoding, so for the
/// same trained state the device search must return the CPU index's labels exactly and its
/// distances up to summation order. With no GPU present ILGPU runs the same kernels on its CPU
/// accelerator, so these exercise the kernels everywhere the suite runs.
/// </summary>
public class GpuIvfTests
{
    private const int D = 32;
    private const int N = 3000;
    private const int Nq = 40;
    private const int Nlist = 32;
    private const int K = 10;

    /// <summary>Database and queries drawn from one distribution: n + nq vectors generated together, then split.</summary>
    private static (float[] Data, float[] Queries) Dataset(MetricType metric, int n = N, int nq = Nq, long seed = 42)
    {
        var all = TestData.Clustered(n + nq, D, seed: seed);
        if (metric == MetricType.InnerProduct) FaissNet.NormalizeL2(all, D);
        return (all.AsSpan(0, n * D).ToArray(), all.AsSpan(n * D, nq * D).ToArray());
    }

    private static IndexIVFFlat TrainedIvfFlat(MetricType metric, float[] data, int nlist = Nlist)
    {
        var index = new IndexIVFFlat(D, nlist, metric);
        index.Train(data);
        index.Add(data);
        return index;
    }

    private static IndexIVFPQ TrainedIvfPq(MetricType metric, float[] data, int nlist = Nlist)
    {
        var index = new IndexIVFPQ(D, nlist, m: 8, nbits: 8, metric);
        index.Train(data);
        index.Add(data);
        return index;
    }

    /// <summary>
    /// Same results up to floating-point summation order. Distances must agree position by position
    /// within 1e-3 relative, padding sentinels exactly. Labels must agree position by position, except
    /// that candidates whose scores tie within that tolerance may come out in either order — the CPU
    /// and GPU add the same terms in a different order, so a near-tie can flip. A swapped label must
    /// still be one the CPU returned at a tied score, or tie the CPU's last score at the k boundary.
    /// </summary>
    private static void AssertSame(SearchResult expected, SearchResult actual)
    {
        Assert.Equal(expected.Labels.Length, actual.Labels.Length);
        int k = expected.K;
        for (int i = 0; i < expected.Distances.Length; i++)
        {
            float e = expected.Distances[i], a = actual.Distances[i];
            if (expected.Labels[i] < 0 || actual.Labels[i] < 0)
            {
                Assert.Equal(expected.Labels[i], actual.Labels[i]);
                Assert.Equal(e, a);
                continue;
            }
            Assert.True(Math.Abs(e - a) <= Tolerance(e), $"distance {i}: cpu {e}, gpu {a}");
            if (expected.Labels[i] == actual.Labels[i]) continue;

            int row = i / k;
            bool tied = false;
            for (int j = row * k; j < (row + 1) * k && !tied; j++)
                tied = expected.Labels[j] == actual.Labels[i] && Math.Abs(expected.Distances[j] - a) <= Tolerance(a);
            float last = expected.Distances[(row + 1) * k - 1];
            tied |= Math.Abs(last - a) <= Tolerance(a);
            Assert.True(tied, $"label {i}: cpu {expected.Labels[i]} ({e}), gpu {actual.Labels[i]} ({a}) is not a tie");
        }

        // Every row holds the same number of results.
        for (int row = 0; row < expected.Labels.Length / k; row++)
            Assert.Equal(expected.Labels.AsSpan(row * k, k).ToArray().Count(l => l >= 0),
                         actual.Labels.AsSpan(row * k, k).ToArray().Count(l => l >= 0));
    }

    private static float Tolerance(float value) => 1e-3f * Math.Max(1f, Math.Abs(value));

    public static TheoryData<MetricType> Metrics => new() { MetricType.L2, MetricType.InnerProduct };

    // ------------------------------------------------------------ IVFFlat

    [Theory]
    [MemberData(nameof(Metrics))]
    public void GpuIvfFlatMatchesTheCpuIndex(MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        var cpu = TrainedIvfFlat(metric, data);
        cpu.Nprobe = 4;
        using var gpu = GpuIndexIVFFlat.FromCpu(cpu);

        Assert.Equal(cpu.Ntotal, gpu.Ntotal);
        Assert.Equal(4, gpu.Nprobe);
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public void GpuIvfFlatBuiltOnTheGpuMatchesOneBuiltOnTheCpu(MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        using var gpu = new GpuIndexIVFFlat(D, Nlist, metric);
        Assert.False(gpu.IsTrained);
        gpu.Train(data);
        gpu.Add(data);
        gpu.Nprobe = 3;

        var cpu = gpu.ToCpu();
        Assert.Equal(3, cpu.Nprobe);
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
        // Recall sanity against exact search, so a matching-but-broken pair cannot pass.
        Assert.True(FaissNet.ComputeRecall(TestData.GroundTruth(data, queries, D, K, metric), gpu.Search(queries, K)) > 0.5);
    }

    // ------------------------------------------------------------ IVFPQ

    [Theory]
    [MemberData(nameof(Metrics))]
    public void GpuIvfPqMatchesTheCpuIndex(MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        var cpu = TrainedIvfPq(metric, data);
        cpu.Nprobe = 4;
        using var gpu = GpuIndexIVFPQ.FromCpu(cpu);

        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
    }

    [Fact]
    public void GpuIvfPqMatchesThePrecomputedTableCpuPath()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var cpu = TrainedIvfPq(MetricType.L2, data);
        cpu.Nprobe = 4;
        using var gpu = GpuIndexIVFPQ.FromCpu(cpu);

        cpu.UsePrecomputedTable = true;
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
    }

    [Fact]
    public void GpuIvfPqBuiltOnTheGpuMatchesOneBuiltOnTheCpu()
    {
        var (data, queries) = Dataset(MetricType.L2);
        using var gpu = new GpuIndexIVFPQ(D, Nlist, m: 8);
        gpu.Train(data);
        gpu.Add(data);
        gpu.Nprobe = 4;

        var cpu = gpu.ToCpu();
        Assert.Equal(cpu.Pq.Centroids, gpu.Pq.Centroids);
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
        Assert.True(FaissNet.ComputeRecall(TestData.GroundTruth(data, queries, D, K), gpu.Search(queries, K)) > 0.3);
    }

    [Fact]
    public void GpuIvfPqRejectsCodeWidthsOtherThanEightBits()
    {
        Assert.Throws<NotSupportedException>(() => new GpuIndexIVFPQ(D, Nlist, m: 8, nbits: 4));

        var cpu = new IndexIVFPQ(D, Nlist, m: 8, nbits: 6);
        Assert.Throws<NotSupportedException>(() => GpuIndexIVFPQ.FromCpu(cpu));
    }

    // ------------------------------------------------------------ Parameters and filtering

    public static TheoryData<string, MetricType> KindsAndMetrics => new()
    {
        { "flat", MetricType.L2 }, { "flat", MetricType.InnerProduct },
        { "pq", MetricType.L2 }, { "pq", MetricType.InnerProduct },
    };

    private static (IndexIVF Cpu, GpuIndexIVF Gpu) Pair(string kind, MetricType metric, float[] data)
    {
        if (kind == "flat")
        {
            var cpu = TrainedIvfFlat(metric, data);
            return (cpu, GpuIndexIVFFlat.FromCpu(cpu));
        }
        var pq = TrainedIvfPq(metric, data);
        return (pq, GpuIndexIVFPQ.FromCpu(pq));
    }

    [Theory]
    [MemberData(nameof(KindsAndMetrics))]
    public void SelectorsGiveTheSameResultsAsTheCpuIndex(string kind, MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        var (cpu, gpu) = Pair(kind, metric, data);
        using var _ = (IDisposable)gpu;

        IDSelector[] selectors =
        [
            IDSelector.Range(500, 1700),
            IDSelector.Predicate(id => id % 3 == 0),
            IDSelector.Batch(Enumerable.Range(0, N).Where(i => i % 7 == 1).Select(i => (long)i)),
            IDSelector.Range(N + 10, N + 20), // matches nothing: every label must be -1
        ];

        foreach (var sel in selectors)
        {
            var p = new SearchParametersIVF { Sel = sel, Nprobe = 6 };
            var expected = cpu.Search(queries, K, p);
            var actual = gpu.Search(queries, K, p);
            AssertSame(expected, actual);
            foreach (long label in actual.Labels) Assert.True(label < 0 || sel.IsMember(label));
        }

        Assert.All(gpu.Search(queries, K, IDSelector.Range(N + 10, N + 20)).Labels, label => Assert.Equal(-1, label));
    }

    [Theory]
    [MemberData(nameof(KindsAndMetrics))]
    public void NprobeOverrideAppliesToOneCallOnly(string kind, MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        var (cpu, gpu) = Pair(kind, metric, data);
        using var _ = (IDisposable)gpu;
        Assert.Equal(1, gpu.Nprobe);

        var wide = new SearchParametersIVF { Nprobe = 12 };
        AssertSame(cpu.Search(queries, K, wide), gpu.Search(queries, K, wide));
        Assert.Equal(1, gpu.Nprobe);

        // Default parameters fall back to the index's own nprobe.
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K, new SearchParameters()));

        // An override beyond nlist is clamped, as on the CPU: an exhaustive scan.
        var all = new SearchParametersIVF { Nprobe = Nlist * 4 };
        AssertSame(cpu.Search(queries, K, all), gpu.Search(queries, K, all));
    }

    [Fact]
    public void ParametersTheGpuCannotHonourAreRejectedLoudly()
    {
        var (data, queries) = Dataset(MetricType.L2, n: 500);
        using var gpu = GpuIndexIVFFlat.FromCpu(TrainedIvfFlat(MetricType.L2, data, nlist: 8));

        Assert.Throws<NotSupportedException>(() => gpu.Search(queries, K, new SearchParametersIVF { MaxCodes = 100 }));
        Assert.Throws<NotSupportedException>(() => gpu.Search(queries, K, new SearchParametersHNSW { EfSearch = 16 }));
        Assert.Throws<NotSupportedException>(() => gpu.Search(queries, K,
            new SearchParametersIVF { QuantizerParams = new SearchParametersHNSW { EfSearch = 16 } }));
        // Default-valued parameter objects are accepted.
        gpu.Search(queries, K, new SearchParametersHNSW());
    }

    // ------------------------------------------------------------ Edge cases

    [Theory]
    [MemberData(nameof(KindsAndMetrics))]
    public void KAtOneAndAtALargeValueMatchTheCpuIndex(string kind, MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        var (cpu, gpu) = Pair(kind, metric, data);
        using var _ = (IDisposable)gpu;

        var p = new SearchParametersIVF { Nprobe = 2 };
        AssertSame(cpu.Search(queries, 1, p), gpu.Search(queries, 1, p));

        // 400 exceeds what two lists usually hold, so rows are padded with -1 — and the row stride
        // must stay k, not shrink to the number found.
        var expected = cpu.Search(queries, 400, p);
        var actual = gpu.Search(queries, 400, p);
        Assert.Contains(-1L, expected.Labels);
        AssertSame(expected, actual);
    }

    [Theory]
    [MemberData(nameof(KindsAndMetrics))]
    public void EmptyListsAreScannedCorrectly(string kind, MetricType metric)
    {
        var (data, queries) = Dataset(metric);
        IndexIVF cpu = kind == "flat"
            ? new IndexIVFFlat(D, 64, metric)
            : new IndexIVFPQ(D, 64, m: 8, nbits: 8, metric);
        cpu.Train(data);
        // Only a sliver of the data is added, so most of the 64 lists stay empty.
        cpu.Add(data.AsSpan(0, 40 * D));
        Assert.True(cpu.ListStatistics().Empty > 0);

        using var gpu = kind == "flat"
            ? (GpuIndexIVF)GpuIndexIVFFlat.FromCpu((IndexIVFFlat)cpu)
            : GpuIndexIVFPQ.FromCpu((IndexIVFPQ)cpu);

        foreach (int nprobe in new[] { 1, 5, 64 })
        {
            var p = new SearchParametersIVF { Nprobe = nprobe };
            AssertSame(cpu.Search(queries, K, p), gpu.Search(queries, K, p));
        }
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public void AnEmptyIndexReturnsMinusOneLabels(MetricType metric)
    {
        var (data, queries) = Dataset(metric, n: 600);
        using var flat = new GpuIndexIVFFlat(D, 8, metric);
        flat.Train(data);
        using var pq = new GpuIndexIVFPQ(D, 8, m: 8, metric: metric);
        pq.Train(data);

        float sentinel = metric == MetricType.InnerProduct ? float.MinValue : float.MaxValue;
        foreach (GpuIndexIVF index in new GpuIndexIVF[] { flat, pq })
        {
            Assert.Equal(0, index.Ntotal);
            var result = index.Search(queries, K);
            Assert.All(result.Labels, label => Assert.Equal(-1, label));
            Assert.All(result.Distances, distance => Assert.Equal(sentinel, distance));

            // And again after content came and went.
            index.Add(data);
            Assert.DoesNotContain(-1L, index.Search(queries, 1).Labels);
            index.Reset();
            Assert.All(index.Search(queries, K).Labels, label => Assert.Equal(-1, label));
        }
    }

    [Fact]
    public void SearchingAnUntrainedIndexThrows()
    {
        using var gpu = new GpuIndexIVFFlat(D, Nlist);
        Assert.Throws<InvalidOperationException>(() => gpu.Search(new float[D], 1));
    }

    // ------------------------------------------------------------ Copies and re-upload

    [Fact]
    public void IvfFlatRoundTripsThroughTheGpu()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var cpu = TrainedIvfFlat(MetricType.L2, data);
        cpu.Nprobe = 5;
        var expected = cpu.Search(queries, K);

        using var gpu = GpuIndexIVFFlat.FromCpu(cpu);
        var back = gpu.ToCpu();
        Assert.Equal(cpu.Ntotal, back.Ntotal);
        Assert.Equal(5, back.Nprobe);
        AssertSame(expected, back.Search(queries, K));

        // Copies are independent in both directions.
        cpu.Reset();
        AssertSame(expected, gpu.Search(queries, K));
        gpu.Reset();
        AssertSame(expected, back.Search(queries, K));
    }

    [Fact]
    public void IvfPqRoundTripsThroughTheGpu()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var cpu = TrainedIvfPq(MetricType.L2, data);
        cpu.Nprobe = 5;

        using var gpu = GpuIndexIVFPQ.FromCpu(cpu);
        var back = gpu.ToCpu();
        Assert.Equal(cpu.Pq.Centroids, back.Pq.Centroids);
        AssertSame(cpu.Search(queries, K), back.Search(queries, K));
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
    }

    [Fact]
    public void AnUntrainedIndexCopiesToTheGpuAndTrainsThere()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var cpu = new IndexIVFPQ(D, Nlist, m: 8);
        using var gpu = GpuIndexIVFPQ.FromCpu(cpu);
        Assert.False(gpu.IsTrained);

        gpu.Train(data);
        gpu.Add(data);
        Assert.False(cpu.IsTrained); // the source was copied, not trained through
        AssertSame(gpu.ToCpu().Search(queries, K), gpu.Search(queries, K));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("pq")]
    public void MutationsAfterASearchAreUploadedBeforeTheNext(string kind)
    {
        var (data, queries) = Dataset(MetricType.L2);
        var half = data.AsSpan(0, N / 2 * D).ToArray();
        var rest = data.AsSpan(N / 2 * D).ToArray();

        IndexIVF cpu = kind == "flat" ? new IndexIVFFlat(D, Nlist) : new IndexIVFPQ(D, Nlist, m: 8);
        cpu.Train(data);
        using GpuIndexIVF gpu = kind == "flat"
            ? GpuIndexIVFFlat.FromCpu((IndexIVFFlat)cpu)
            : GpuIndexIVFPQ.FromCpu((IndexIVFPQ)cpu);

        cpu.Add(half);
        gpu.Add(half);
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));

        var ids = Enumerable.Range(0, rest.Length / D).Select(i => 100_000L + i).ToArray();
        cpu.AddWithIds(rest, ids);
        gpu.AddWithIds(rest, ids);
        Assert.Equal(cpu.Ntotal, gpu.Ntotal);
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));

        Assert.Equal(cpu.RemoveIds(id => id % 2 == 0), gpu.RemoveIds(id => id % 2 == 0));
        AssertSame(cpu.Search(queries, K), gpu.Search(queries, K));
    }

    [Fact]
    public void QueryChunkingDoesNotChangeResults()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var flat = TrainedIvfFlat(MetricType.L2, data);
        var pq = TrainedIvfPq(MetricType.L2, data);
        var p = new SearchParametersIVF { Nprobe = 4 };

        // A budget of a few kilobytes forces one query per chunk, so every chunk boundary is crossed.
        using var resources = new StandardGpuResources(preferCpu: true) { MaxDistanceMatrixBytes = 4096 };
        using var gpuFlat = GpuIndexIVFFlat.FromCpu(flat, resources);
        using var gpuPq = GpuIndexIVFPQ.FromCpu(pq, resources);

        AssertSame(flat.Search(queries, K, p), gpuFlat.Search(queries, K, p));
        AssertSame(pq.Search(queries, K, p), gpuPq.Search(queries, K, p));
    }

    [Fact]
    public void GpuFlatSelectorMatchesTheCpuIndexUnderChunking()
    {
        var (data, queries) = Dataset(MetricType.L2);
        var cpu = new IndexFlatL2(D);
        cpu.Add(data);

        using var resources = new StandardGpuResources(preferCpu: true) { MaxDistanceMatrixBytes = 3 * N * sizeof(float) };
        using var gpu = new IndexFlatL2Gpu(D, resources);
        gpu.Add(data);

        var sel = IDSelector.Predicate(id => id % 5 == 2);
        AssertSame(cpu.Search(queries, K, sel), gpu.Search(queries, K, sel));
    }
}
