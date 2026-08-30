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
/// </summary>
public class GpuIndexFlat : Index, IDisposable
{
    private readonly StandardGpuResources _resources;
    private readonly List<float> _staging = [];
    private MemoryBuffer1D<float, Stride1D.Dense>? _deviceVectors;
    private bool _dirty;
    private bool _disposed;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int> _distanceKernel;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, int, int, int, int> _selectKernel;

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
        IsTrained = true;

        _distanceKernel = _resources.Accelerator
            .LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int>(DistanceKernel);
        _selectKernel = _resources.Accelerator
            .LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, int, int, int, int>(SelectKernel);
    }

    public override bool SupportsReconstruct => true;

    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        _staging.AddRange(x);
        Ntotal = _staging.Count / D;
        _dirty = true;
    }

    /// <summary>Uploads pending vectors. Called automatically before the first search after an add.</summary>
    public void Sync()
    {
        if (!_dirty) return;
        _deviceVectors?.Dispose();
        _deviceVectors = _resources.Accelerator.Allocate1D(_staging.ToArray());
        _dirty = false;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nq == 0) return;
        if (Ntotal == 0)
        {
            distances.Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels.Fill(-1);
            return;
        }

        Sync();
        int ntotal = (int)Ntotal;
        if (k > MaxK)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"The GPU selection kernel keeps k <= {MaxK} per thread; use the CPU index for larger k.");

        // Chunk queries so the distance matrix stays inside the configured device-memory budget.
        long perQueryBytes = (long)ntotal * sizeof(float);
        int chunk = (int)Math.Clamp(_resources.MaxDistanceMatrixBytes / Math.Max(1, perQueryBytes), 1, nq);

        var accelerator = _resources.Accelerator;
        var stream = accelerator.DefaultStream;
        using var deviceDistances = accelerator.Allocate1D<float>((long)chunk * ntotal);
        using var deviceQueries = accelerator.Allocate1D<float>((long)chunk * D);
        using var outDistances = accelerator.Allocate1D<float>((long)chunk * k);
        using var outLabels = accelerator.Allocate1D<long>((long)chunk * k);

        var hostDistances = new float[(long)chunk * k];
        var hostLabels = new long[(long)chunk * k];
        int metric = MetricType == MetricType.InnerProduct ? 1 : 0;

        for (int start = 0; start < nq; start += chunk)
        {
            int count = Math.Min(chunk, nq - start);
            deviceQueries.View.SubView(0, (long)count * D)
                .CopyFromCPU(stream, queries.Slice(start * D, count * D).ToArray());

            _distanceKernel(stream, new Index2D(count, ntotal),
                deviceQueries.View, _deviceVectors!.View, deviceDistances.View, D, ntotal, metric);

            _selectKernel(stream, new Index1D(count),
                deviceDistances.View, outDistances.View, outLabels.View, ntotal, k, metric, count);

            stream.Synchronize();

            outDistances.View.SubView(0, (long)count * k).CopyToCPU(stream, hostDistances.AsSpan(0, count * k));
            outLabels.View.SubView(0, (long)count * k).CopyToCPU(stream, hostLabels.AsSpan(0, count * k));
            stream.Synchronize();

            hostDistances.AsSpan(0, count * k).CopyTo(distances.Slice(start * k, count * k));
            hostLabels.AsSpan(0, count * k).CopyTo(labels.Slice(start * k, count * k));
        }
    }

    /// <summary>Upper bound on k, set by the fixed-size selection array each kernel thread holds.</summary>
    public const int MaxK = 1024;

    /// <summary>One thread per (query, database vector) pair; writes the distance matrix.</summary>
    private static void DistanceKernel(
        Index2D index, ArrayView<float> queries, ArrayView<float> database, ArrayView<float> distances,
        int d, int ntotal, int metric)
    {
        int q = index.X;
        int j = index.Y;
        if (j >= ntotal) return;

        long queryOffset = (long)q * d;
        long vectorOffset = (long)j * d;
        float sum = 0f;

        if (metric == 1)
        {
            for (int i = 0; i < d; i++) sum += queries[queryOffset + i] * database[vectorOffset + i];
        }
        else
        {
            for (int i = 0; i < d; i++)
            {
                float diff = queries[queryOffset + i] - database[vectorOffset + i];
                sum += diff * diff;
            }
        }

        distances[(long)q * ntotal + j] = sum;
    }

    /// <summary>
    /// One thread per query: a single pass over that query's distance row, maintaining the k best in
    /// a small insertion-sorted window. Keeping selection on the device is what avoids shipping the
    /// full distance matrix back across the bus.
    /// </summary>
    private static void SelectKernel(
        Index1D index, ArrayView<float> distances, ArrayView<float> outDistances, ArrayView<long> outLabels,
        int ntotal, int k, int metric, int nq)
    {
        int q = index.X;
        if (q >= nq) return;

        long rowOffset = (long)q * ntotal;
        long outOffset = (long)q * k;
        bool similarity = metric == 1;
        float worstSentinel = similarity ? float.MinValue : float.MaxValue;

        for (int i = 0; i < k; i++)
        {
            outDistances[outOffset + i] = worstSentinel;
            outLabels[outOffset + i] = -1;
        }

        for (int j = 0; j < ntotal; j++)
        {
            float score = distances[rowOffset + j];
            float worst = outDistances[outOffset + k - 1];
            bool better = similarity ? score > worst : score < worst;
            if (!better) continue;

            int position = k - 1;
            while (position > 0)
            {
                float previous = outDistances[outOffset + position - 1];
                bool shift = similarity ? score > previous : score < previous;
                if (!shift) break;
                outDistances[outOffset + position] = previous;
                outLabels[outOffset + position] = outLabels[outOffset + position - 1];
                position--;
            }
            outDistances[outOffset + position] = score;
            outLabels[outOffset + position] = j;
        }
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= Ntotal) throw new ArgumentOutOfRangeException(nameof(key));
        CollectionsMarshal.AsSpan(_staging).Slice((int)key * D, D).CopyTo(output);
    }

    public override void Reset()
    {
        _staging.Clear();
        _deviceVectors?.Dispose();
        _deviceVectors = null;
        _dirty = false;
        Ntotal = 0;
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
        if (_disposed) return;
        _disposed = true;
        _deviceVectors?.Dispose();
        _deviceVectors = null;
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
