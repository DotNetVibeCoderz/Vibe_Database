using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace Faiss.Net.Gpu;

/// <summary>
/// Exhaustive search executed on the GPU — a drop-in replacement for <see cref="IndexFlat"/>.
/// <para>
/// Brute-force search is the ideal GPU workload: every candidate is independent, the arithmetic is
/// a pure multiply-add chain, and the access pattern is a straight sequential read of the database.
/// It is also memory-bandwidth-bound, which is precisely where a GPU has an order-of-magnitude
/// advantage over a CPU, so the speedup is largest exactly where the CPU path hurts most.
/// </para>
/// <para>
/// Two kernels run per query chunk. The first fills a <c>chunk x ntotal</c> distance matrix, one
/// thread per (query, vector) pair. The second selects the top k per query, one thread per query,
/// so only <c>chunk * k</c> results cross the bus instead of the whole matrix — the transfer, not
/// the arithmetic, is what would otherwise dominate.
/// </para>
/// <para>
/// Vectors live in device memory, so <see cref="Add"/> re-uploads the database. Build the index
/// once and query it many times; that is the shape this is built for.
/// </para>
/// <para>
/// Device work is serialized per <see cref="StandardGpuResources"/>: every index on the same
/// resources shares the accelerator's default stream, so two searches at once would gain nothing
/// and could interleave one index's upload with another's scan.
/// </para>
/// </summary>
public class GpuIndexFlat : Index, IDisposable
{
    private readonly StandardGpuResources _resources;
    private readonly List<float> _staging = [];
    private MemoryBuffer1D<float, Stride1D.Dense>? _deviceVectors;
    private bool _dirty;
    private bool _disposed;

    private readonly object _gate;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int> _distanceKernel;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, ArrayView<byte>, int, int, int, int, int> _selectKernel;

    /// <summary>The accelerator this index runs on.</summary>
    public StandardGpuResources Resources => _resources;

    /// <summary>True when a real GPU is in use rather than the CPU fallback accelerator.</summary>
    public bool IsHardwareAccelerated => _resources.IsHardwareAccelerated;

    public GpuIndexFlat(int dimension, MetricType metric = MetricType.L2, StandardGpuResources? resources = null)
        : base(dimension, metric)
    {
        if (metric is not (MetricType.L2 or MetricType.InnerProduct))
            throw new NotSupportedException("The GPU backend supports L2 and inner product.");

        _resources = resources ?? StandardGpuResources.Default;
        _gate = _resources.SyncRoot;
        IsTrained = true;

        _distanceKernel = _resources.Accelerator
            .LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int>(GpuKernels.Distance);
        _selectKernel = _resources.Accelerator
            .LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, ArrayView<byte>, int, int, int, int, int>(GpuKernels.Select);
    }

    public override bool SupportsReconstruct => true;

    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        lock (_gate)
        {
            _staging.AddRange(x);
            Ntotal = _staging.Count / D;
            _dirty = true;
        }
    }

    /// <summary>Uploads pending vectors. Called automatically before the first search after an add.</summary>
    public void Sync()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _deviceVectors?.Dispose();
            _deviceVectors = _resources.Accelerator.Allocate1D(_staging.ToArray());
            _dirty = false;
        }
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    /// <summary>
    /// Search with per-call options. A selector in <paramref name="parameters"/> is evaluated on the
    /// host into one byte per stored vector, uploaded, and tested inside the selection kernel, so
    /// each query still receives up to k members of the subset — the same contract as the CPU
    /// <see cref="IndexFlat"/>. Evaluating on the host is what lets any selector work, predicates
    /// included, at the price of one <see cref="IDSelector.IsMember"/> call per vector per search.
    /// Like the CPU flat index, options other than the selector are ignored: there is nothing else
    /// to tune in an exhaustive scan.
    /// </summary>
    public override void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nq == 0) return;
        if (Ntotal == 0)
        {
            distances[..(nq * k)].Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        if (k > MaxK)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"The GPU selection kernel keeps k <= {MaxK} per thread; use the CPU index for larger k.");

        lock (_gate)
        {
            Sync();
            int ntotal = (int)Ntotal;

            // Chunk queries so the distance matrix stays inside the configured device-memory budget.
            long perQueryBytes = (long)ntotal * sizeof(float);
            int chunk = (int)Math.Clamp(_resources.MaxDistanceMatrixBytes / Math.Max(1, perQueryBytes), 1, nq);

            var accelerator = _resources.Accelerator;
            var stream = accelerator.DefaultStream;
            var selector = parameters?.Sel;
            using var deviceMask = selector is null ? accelerator.Allocate1D<byte>(1) : UploadMask(selector, ntotal);
            using var deviceDistances = accelerator.Allocate1D<float>((long)chunk * ntotal);
            using var deviceQueries = accelerator.Allocate1D<float>((long)chunk * D);
            using var outDistances = accelerator.Allocate1D<float>((long)chunk * k);
            using var outLabels = accelerator.Allocate1D<long>((long)chunk * k);

            var hostDistances = new float[(long)chunk * k];
            var hostLabels = new long[(long)chunk * k];
            int metric = GpuKernels.MetricCode(MetricType);
            int useMask = selector is null ? 0 : 1;

            for (int start = 0; start < nq; start += chunk)
            {
                int count = Math.Min(chunk, nq - start);
                deviceQueries.View.SubView(0, (long)count * D)
                    .CopyFromCPU(stream, queries.Slice(start * D, count * D).ToArray());

                _distanceKernel(stream, new Index2D(count, ntotal),
                    deviceQueries.View, _deviceVectors!.View, deviceDistances.View, D, ntotal, metric);

                _selectKernel(stream, new Index1D(count),
                    deviceDistances.View, outDistances.View, outLabels.View, deviceMask.View,
                    ntotal, k, metric, count, useMask);

                stream.Synchronize();

                outDistances.View.SubView(0, (long)count * k).CopyToCPU(stream, hostDistances.AsSpan(0, count * k));
                outLabels.View.SubView(0, (long)count * k).CopyToCPU(stream, hostLabels.AsSpan(0, count * k));
                stream.Synchronize();

                hostDistances.AsSpan(0, count * k).CopyTo(distances.Slice(start * k, count * k));
                hostLabels.AsSpan(0, count * k).CopyTo(labels.Slice(start * k, count * k));
            }
        }
    }

    /// <summary>One membership byte per stored vector; the flat index's ids are its positions.</summary>
    private MemoryBuffer1D<byte, Stride1D.Dense> UploadMask(IDSelector selector, int ntotal)
    {
        var mask = new byte[ntotal];
        if (selector.MayIntersect(0, ntotal - 1))
            for (int i = 0; i < ntotal; i++) mask[i] = selector.IsMember(i) ? (byte)1 : (byte)0;
        return _resources.Accelerator.Allocate1D(mask);
    }

    /// <summary>
    /// Upper bound on k. Each selection thread keeps its window in global memory, insertion-sorted,
    /// so the cost of a candidate that enters the window grows with k.
    /// </summary>
    public const int MaxK = 1024;

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= Ntotal) throw new ArgumentOutOfRangeException(nameof(key));
        CollectionsMarshal.AsSpan(_staging).Slice((int)key * D, D).CopyTo(output);
    }

    public override void Reset()
    {
        lock (_gate)
        {
            _staging.Clear();
            _deviceVectors?.Dispose();
            _deviceVectors = null;
            _dirty = false;
            Ntotal = 0;
        }
    }

    /// <summary>Copies this index back to a CPU <see cref="IndexFlat"/> — the <c>index_gpu_to_cpu</c> direction.</summary>
    public IndexFlat ToCpu()
    {
        var index = MetricType == MetricType.InnerProduct
            ? (IndexFlat)new IndexFlatIP(D)
            : new IndexFlatL2(D);
        index.Add(CollectionsMarshal.AsSpan(_staging));
        return index;
    }

    /// <summary>Copies a CPU flat index onto the GPU — the <c>index_cpu_to_gpu</c> direction.</summary>
    public static GpuIndexFlat FromCpu(IndexFlat index, StandardGpuResources? resources = null)
    {
        var gpu = new GpuIndexFlat(index.D, index.MetricType, resources);
        gpu.Add(index.Vectors);
        return gpu;
    }

    public override long MemoryUsage => (long)_staging.Count * sizeof(float) * 2; // host staging plus device copy

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, {MetricType.ToShortString()}, device={_resources.DeviceName})";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _deviceVectors?.Dispose();
            _deviceVectors = null;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>GPU squared-L2 exact search. Drop-in replacement for <see cref="IndexFlatL2"/>.</summary>
public sealed class IndexFlatL2Gpu : GpuIndexFlat
{
    public IndexFlatL2Gpu(int dimension, StandardGpuResources? resources = null)
        : base(dimension, MetricType.L2, resources) { }
}

/// <summary>GPU inner-product exact search. Drop-in replacement for <see cref="IndexFlatIP"/>.</summary>
public sealed class IndexFlatIPGpu : GpuIndexFlat
{
    public IndexFlatIPGpu(int dimension, StandardGpuResources? resources = null)
        : base(dimension, MetricType.InnerProduct, resources) { }
}
