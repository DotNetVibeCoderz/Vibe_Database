using BenchmarkDotNet.Attributes;
using Faiss.Net;
using Faiss.Net.Core;

namespace Faiss.Net.Benchmarks;

/// <summary>
/// The SIMD kernels in isolation. These are the floor everything else stands on: no index can be
/// faster than the distance function it calls, so a regression here shows up everywhere at once.
/// Dimensions are chosen to cover the register widths and their remainders.
/// </summary>
[SimpleJob]
[MemoryDiagnoser(displayGenColumns: false)]
public class DistanceKernelBenchmarks
{
    private float[] _a = [];
    private float[] _b = [];

    [Params(64, 128, 384, 768, 1536)]
    public int Dimension { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _a = FaissNet.RandomVectors(1, Dimension, seed: 1);
        _b = FaissNet.RandomVectors(1, Dimension, seed: 2);
    }

    [Benchmark(Baseline = true)]
    public float ScalarL2()
    {
        float sum = 0;
        for (int i = 0; i < _a.Length; i++)
        {
            float diff = _a[i] - _b[i];
            sum += diff * diff;
        }
        return sum;
    }

    [Benchmark]
    public float SimdL2() => VectorOps.L2Sqr(_a, _b);

    [Benchmark]
    public float SimdInnerProduct() => VectorOps.InnerProduct(_a, _b);
}

/// <summary>
/// Search latency for a single query, the number an interactive application actually feels.
/// Batch throughput is measured separately by the matched suite; the two are different regimes and
/// an index can be good at one and poor at the other.
/// </summary>
[SimpleJob]
[MemoryDiagnoser(displayGenColumns: false)]
public class SingleQuerySearchBenchmarks
{
    private const int Dimension = 128;
    private const int DatabaseSize = 100_000;

    private float[] _query = [];
    private IndexFlatL2 _flat = null!;
    private IndexIVFFlat _ivfFlat = null!;
    private IndexIVFPQ _ivfPq = null!;
    private IndexHNSWFlat _hnsw = null!;
    private IndexScalarQuantizer _sq = null!;

    [Params(10, 100)]
    public int K { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var dataset = Dataset.Generate(Dimension, DatabaseSize, 1, K);
        _query = dataset.Queries;

        _flat = new IndexFlatL2(Dimension);
        _flat.Add(dataset.Database);

        _ivfFlat = new IndexIVFFlat(Dimension, 316) { Nprobe = 8 };
        _ivfFlat.Train(dataset.Database);
        _ivfFlat.Add(dataset.Database);

        _ivfPq = new IndexIVFPQ(Dimension, 316, m: 16) { Nprobe = 8 };
        _ivfPq.Train(dataset.Database);
        _ivfPq.Add(dataset.Database);

        _hnsw = new IndexHNSWFlat(Dimension, 32) { EfConstruction = 80, EfSearch = 64 };
        _hnsw.Add(dataset.Database);

        _sq = new IndexScalarQuantizer(Dimension);
        _sq.Train(dataset.Database);
        _sq.Add(dataset.Database);
    }

    [Benchmark(Baseline = true)]
    public SearchResult Flat() => _flat.Search(_query, K);

    [Benchmark]
    public SearchResult IvfFlat() => _ivfFlat.Search(_query, K);

    [Benchmark]
    public SearchResult IvfPq() => _ivfPq.Search(_query, K);

    [Benchmark]
    public SearchResult ScalarQuantizer() => _sq.Search(_query, K);

    [Benchmark]
    public SearchResult Hnsw() => _hnsw.Search(_query, K);
}

/// <summary>
/// Batch search: the shape of an offline job or a server handling concurrent requests. The
/// interesting question here is whether threading scales, which is why the same batch is run with
/// one thread and with all of them.
/// </summary>
[SimpleJob]
public class BatchSearchBenchmarks
{
    private const int Dimension = 128;
    private const int DatabaseSize = 50_000;
    private const int Queries = 1_000;

    private float[] _queries = [];
    private IndexFlatL2 _flat = null!;

    [Params(1, 0)] // 0 means "every core"
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var dataset = Dataset.Generate(Dimension, DatabaseSize, Queries, 10);
        _queries = dataset.Queries;
        _flat = new IndexFlatL2(Dimension);
        _flat.Add(dataset.Database);
    }

    [Benchmark]
    public SearchResult FlatBatch()
    {
        _flat.Threads = Threads;
        return _flat.Search(_queries, 10);
    }
}

/// <summary>Index construction, which for a large corpus dominates total time.</summary>
[SimpleJob(warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser(displayGenColumns: false)]
public class BuildBenchmarks
{
    private const int Dimension = 128;
    private float[] _data = [];

    [Params(20_000)]
    public int DatabaseSize { get; set; }

    [GlobalSetup]
    public void Setup() => _data = Dataset.Generate(Dimension, DatabaseSize, 1, 10).Database;

    [Benchmark(Baseline = true)]
    public Index BuildFlat()
    {
        var index = new IndexFlatL2(Dimension);
        index.Add(_data);
        return index;
    }

    [Benchmark]
    public Index BuildIvfFlat()
    {
        var index = new IndexIVFFlat(Dimension, 141);
        index.Train(_data);
        index.Add(_data);
        return index;
    }

    [Benchmark]
    public Index BuildIvfPq()
    {
        var index = new IndexIVFPQ(Dimension, 141, m: 16);
        index.Train(_data);
        index.Add(_data);
        return index;
    }

    [Benchmark]
    public Index BuildHnsw()
    {
        var index = new IndexHNSWFlat(Dimension, 32) { EfConstruction = 40 };
        index.Add(_data);
        return index;
    }
}
