using Faiss.Net.IO;
using ILGPU;
using ILGPU.Runtime;

namespace Faiss.Net.Gpu;

/// <summary>
/// Scalars handed to the IVF kernels as one value. ILGPU passes an unmanaged struct by value, and
/// bundling keeps every kernel signature inside the arity ILGPU's typed loaders support.
/// </summary>
internal struct IvfKernelArgs
{
    public int D;
    public int Nprobe;
    public int K;
    public int Metric;
    public int Count;
    public int UseMask;
    public int M;
    public int Ksub;
    public int Dsub;
    public int ByResidual;
}

/// <summary>
/// Shared machinery of the GPU inverted-file indexes, <see cref="GpuIndexIVFFlat"/> and
/// <see cref="GpuIndexIVFPQ"/>.
/// <para>
/// Each GPU index owns a private CPU <see cref="IndexIVF"/> that does everything except search:
/// training, encoding, adding and removal run there unchanged, so a GPU index and a CPU index built
/// from the same data hold byte-identical codes. That is what lets the search results be compared
/// directly, and it means none of the encoders had to be written twice.
/// </para>
/// <para>
/// Before a search, the lists are flattened into a CSR layout on the device: every code
/// concatenated in list order, plus per-list offsets and sizes, the ids in the same order, and the
/// coarse centroids. A per-list layout would need one allocation per list and a pointer table;
/// CSR is four buffers whatever <c>nlist</c> is. Any mutation marks the copy dirty and the next
/// search re-uploads it — the same lazy scheme as <see cref="GpuIndexFlat.Sync"/>, and the same
/// advice: build once, query many times.
/// </para>
/// <para>
/// A query chunk runs four steps on the device. (1) Coarse distances to every centroid, one thread
/// per (query, centroid). (2) The <c>nprobe</c> nearest centroids per query, one thread per query.
/// (3) A scan with one thread per (query, probe) pair, each walking one list and writing a partial
/// top-k into an <c>[nq, nprobe, k]</c> buffer. (4) A merge that picks the final k of those
/// <c>nprobe * k</c> per query. Only <c>nq * k</c> results cross the bus.
/// </para>
/// </summary>
public abstract class GpuIndexIVF : Index, IDisposable
{
    private readonly object _gate;
    private bool _dirty = true;
    private bool _disposed;

    private MemoryBuffer1D<float, Stride1D.Dense>? _centroids;
    private MemoryBuffer1D<long, Stride1D.Dense>? _ids;
    private MemoryBuffer1D<long, Stride1D.Dense>? _offsets;
    private MemoryBuffer1D<int, Stride1D.Dense>? _sizes;
    private long[] _hostIds = [];
    private long[] _hostOffsets = [];
    private int[] _hostSizes = [];
    private long[] _listMin = [], _listMax = [];
    private long _uploadedEntries;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int> _distanceKernel;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, ArrayView<byte>, int, int, int, int, int> _selectKernel;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<long>, ArrayView<float>, ArrayView<long>, int, int, int, int> _mergeKernel;

    /// <summary>The CPU index that trains, encodes and stores; private so every mutation passes through here and marks the device copy dirty.</summary>
    private protected IndexIVF Cpu { get; }

    /// <summary>The accelerator this index runs on.</summary>
    public StandardGpuResources Resources { get; }

    /// <summary>True when a real GPU is in use rather than the CPU fallback accelerator.</summary>
    public bool IsHardwareAccelerated => Resources.IsHardwareAccelerated;

    private protected GpuIndexIVF(IndexIVF cpu, StandardGpuResources? resources)
        : base(cpu.D, cpu.MetricType)
    {
        if (cpu.MetricType is not (MetricType.L2 or MetricType.InnerProduct))
            throw new NotSupportedException("The GPU backend supports L2 and inner product.");
        if (cpu.Quantizer.MetricType is not (MetricType.L2 or MetricType.InnerProduct))
            throw new NotSupportedException("The GPU coarse quantizer supports L2 and inner product.");

        Cpu = cpu;
        Resources = resources ?? StandardGpuResources.Default;
        // Shared with every index on these resources: they all use one stream. See StandardGpuResources.SyncRoot.
        _gate = Resources.SyncRoot;
        IsTrained = cpu.IsTrained;
        Ntotal = cpu.Ntotal;

        var accelerator = Resources.Accelerator;
        _distanceKernel = accelerator
            .LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int>(GpuKernels.Distance);
        _selectKernel = accelerator
            .LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<long>, ArrayView<byte>, int, int, int, int, int>(GpuKernels.Select);
        _mergeKernel = accelerator
            .LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<long>, ArrayView<float>, ArrayView<long>, int, int, int, int>(GpuKernels.Merge);
    }

    /// <summary>Number of inverted lists.</summary>
    public int Nlist => Cpu.Nlist;

    /// <summary>Lists probed per query; see <see cref="IndexIVF.Nprobe"/>. Overridable per call with <see cref="SearchParametersIVF"/>.</summary>
    public int Nprobe
    {
        get => Cpu.Nprobe;
        set => Cpu.Nprobe = value;
    }

    /// <summary>Clustering settings for the coarse quantizer, forwarded to the CPU index.</summary>
    public ClusteringParameters ClusteringParameters
    {
        get => Cpu.ClusteringParameters;
        set => Cpu.ClusteringParameters = value;
    }

    /// <summary>Bytes per stored code.</summary>
    public int CodeSize => Cpu.CodeSize;

    /// <summary>List balance statistics; see <see cref="InvertedLists.Statistics"/>.</summary>
    public (int Min, int Max, double Mean, int Empty) ListStatistics() => Cpu.ListStatistics();

    // ------------------------------------------------------------ Mutation

    public override void Train(ReadOnlySpan<float> x)
    {
        lock (_gate)
        {
            Cpu.Train(x);
            IsTrained = Cpu.IsTrained;
            _dirty = true;
        }
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        lock (_gate)
        {
            Cpu.Add(x);
            Ntotal = Cpu.Ntotal;
            _dirty = true;
        }
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        lock (_gate)
        {
            Cpu.AddWithIds(x, ids);
            Ntotal = Cpu.Ntotal;
            _dirty = true;
        }
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        lock (_gate)
        {
            long removed = Cpu.RemoveIds(ids);
            Ntotal = Cpu.Ntotal;
            _dirty = true;
            return removed;
        }
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        lock (_gate)
        {
            long removed = Cpu.RemoveIds(predicate);
            Ntotal = Cpu.Ntotal;
            _dirty = true;
            return removed;
        }
    }

    public override void Reset()
    {
        lock (_gate)
        {
            Cpu.Reset();
            Ntotal = 0;
            _dirty = true;
        }
    }

    public override bool SupportsReconstruct => Cpu.SupportsReconstruct;

    /// <summary>Builds the id-to-entry table reconstruction needs; see <see cref="IndexIVF.MakeDirectMap"/>.</summary>
    public void MakeDirectMap()
    {
        lock (_gate) Cpu.MakeDirectMap();
    }

    public override void Reconstruct(long key, Span<float> output) => Cpu.Reconstruct(key, output);

    // ------------------------------------------------------------- Upload

    /// <summary>
    /// Uploads the lists and centroids if anything changed since the last upload. Called
    /// automatically before a search; call it directly to move the upload cost out of the first query.
    /// </summary>
    public void Sync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_dirty) return;
            EnsureTrained();
            ReleaseDevice();

            var accelerator = Resources.Accelerator;
            int nlist = Cpu.Nlist;
            int codeSize = Cpu.CodeSize;
            var lists = Cpu.Lists;

            var offsets = new long[nlist];
            var sizes = new int[nlist];
            long total = 0;
            for (int list = 0; list < nlist; list++)
            {
                offsets[list] = total;
                sizes[list] = lists.ListSize(list);
                total += sizes[list];
            }

            var ids = new long[total];
            var codes = new byte[total * codeSize];
            var listMin = new long[nlist];
            var listMax = new long[nlist];
            for (int list = 0; list < nlist; list++)
            {
                var view = lists.GetList(list);
                view.Ids.CopyTo(ids.AsSpan((int)offsets[list], sizes[list]));
                view.Codes.CopyTo(codes.AsSpan((int)(offsets[list] * codeSize), sizes[list] * codeSize));
                listMin[list] = view.MinId;
                listMax[list] = view.MaxId;
            }

            var centroids = new float[(long)nlist * D];
            if (Cpu.Quantizer is IndexFlat flat)
                flat.Vectors[..centroids.Length].CopyTo(centroids);
            else if (Cpu.Quantizer.SupportsReconstruct)
                for (int list = 0; list < nlist; list++) Cpu.Quantizer.Reconstruct(list, centroids.AsSpan(list * D, D));
            else
                throw new NotSupportedException(
                    $"The coarse quantizer {Cpu.Quantizer.GetType().Name} cannot reconstruct its centroids for upload.");

            // ILGPU rejects zero-length buffers on some back ends; one padding element is never read.
            _centroids = accelerator.Allocate1D(centroids);
            _ids = accelerator.Allocate1D(total == 0 ? [-1L] : ids);
            _offsets = accelerator.Allocate1D(offsets);
            _sizes = accelerator.Allocate1D(sizes);
            UploadCodes(accelerator, total == 0 ? new byte[codeSize] : codes);

            _hostIds = ids;
            _hostOffsets = offsets;
            _hostSizes = sizes;
            _listMin = listMin;
            _listMax = listMax;
            _uploadedEntries = total;
            _dirty = false;
        }
    }

    /// <summary>Uploads the concatenated codes (CSR order) and whatever else the subclass's scan reads.</summary>
    private protected abstract void UploadCodes(Accelerator accelerator, byte[] codes);

    /// <summary>Releases the subclass's device buffers.</summary>
    private protected abstract void ReleaseCodes();

    private void ReleaseDevice()
    {
        _centroids?.Dispose();
        _ids?.Dispose();
        _offsets?.Dispose();
        _sizes?.Dispose();
        _centroids = null;
        _ids = null;
        _offsets = null;
        _sizes = null;
        ReleaseCodes();
    }

    // ------------------------------------------------------------ Search

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    /// <summary>
    /// Search with per-call options. Honoured: <see cref="SearchParametersIVF.Nprobe"/> (for this
    /// call only — the index's own <see cref="Nprobe"/> is not touched) and
    /// <see cref="SearchParameters.Sel"/>. The selector is evaluated on the host into one byte per
    /// stored entry in CSR order, uploaded, and tested inside the scan kernel, so each query still
    /// receives up to k members of the subset; lists whose id range cannot hold a member are left
    /// all-zero without calling the selector at all.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="SearchParametersIVF.MaxCodes"/>, non-default
    /// <see cref="SearchParametersIVF.QuantizerParams"/>, or any other non-default parameter type.
    /// The coarse search runs on the device as an exact scan, so there is nothing to pass quantizer
    /// options to, and a code budget would need a sequential walk over probes that the per-probe
    /// threads do not have.
    /// </exception>
    public override void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureTrained();
        int nprobe = ResolveParameters(parameters);
        if (nq == 0) return;
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");
        if (Ntotal == 0)
        {
            distances[..(nq * k)].Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels[..(nq * k)].Fill(-1);
            return;
        }
        if (k > GpuIndexFlat.MaxK)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"The GPU selection kernels keep k <= {GpuIndexFlat.MaxK} per thread; use the CPU index for larger k.");

        lock (_gate)
        {
            Sync();
            SearchChunks(queries, nq, k, nprobe, parameters?.Sel, distances, labels);
        }
    }

    private int ResolveParameters(SearchParameters? parameters)
    {
        if (parameters is null) return Math.Clamp(Cpu.Nprobe, 1, Cpu.Nlist);
        if (parameters is SearchParametersIVF ivf)
        {
            if (ivf.MaxCodes != 0)
                throw new NotSupportedException($"{GetType().Name} does not support SearchParametersIVF.MaxCodes.");
            if (ivf.QuantizerParams is { IsDefault: false })
                throw new NotSupportedException($"{GetType().Name} runs coarse quantization on the device and cannot honour QuantizerParams.");
            return Math.Clamp(ivf.Nprobe > 0 ? ivf.Nprobe : Cpu.Nprobe, 1, Cpu.Nlist);
        }
        if (parameters.GetType() != typeof(SearchParameters) && !parameters.IsDefault)
            throw new NotSupportedException($"{GetType().Name} does not support {parameters.GetType().Name}.");
        return Math.Clamp(Cpu.Nprobe, 1, Cpu.Nlist);
    }

    private void SearchChunks(
        ReadOnlySpan<float> queries, int nq, int k, int nprobe, IDSelector? selector,
        Span<float> distances, Span<long> labels)
    {
        var accelerator = Resources.Accelerator;
        var stream = accelerator.DefaultStream;
        int nlist = Cpu.Nlist;
        int candidates = nprobe * k;

        // Everything a query occupies on the device: its vector, a coarse-distance row, the probed
        // lists, the partial top-k of every probe, the final top-k, and whatever the subclass adds
        // (the IVFPQ lookup tables). Chunking on the sum keeps the whole working set in budget.
        long perQueryBytes = (long)D * sizeof(float)
            + (long)nlist * sizeof(float)
            + (long)nprobe * (sizeof(float) + sizeof(long))
            + (long)candidates * (sizeof(float) + sizeof(long))
            + (long)k * (sizeof(float) + sizeof(long))
            + ScratchBytesPerQuery(nprobe);
        int chunk = (int)Math.Clamp(Resources.MaxDistanceMatrixBytes / Math.Max(1, perQueryBytes), 1, nq);

        using var deviceMask = selector is null ? accelerator.Allocate1D<byte>(1) : UploadMask(selector);
        using var deviceQueries = accelerator.Allocate1D<float>((long)chunk * D);
        using var coarseDistances = accelerator.Allocate1D<float>((long)chunk * nlist);
        using var probeScores = accelerator.Allocate1D<float>((long)chunk * nprobe);
        using var probeLists = accelerator.Allocate1D<long>((long)chunk * nprobe);
        using var partialDistances = accelerator.Allocate1D<float>((long)chunk * candidates);
        using var partialLabels = accelerator.Allocate1D<long>((long)chunk * candidates);
        using var outDistances = accelerator.Allocate1D<float>((long)chunk * k);
        using var outLabels = accelerator.Allocate1D<long>((long)chunk * k);
        BeginScratch(accelerator, chunk, nprobe);
        try
        {
            var hostDistances = new float[(long)chunk * k];
            var hostLabels = new long[(long)chunk * k];
            int metric = GpuKernels.MetricCode(MetricType);
            int coarseMetric = GpuKernels.MetricCode(Cpu.Quantizer.MetricType);

            for (int start = 0; start < nq; start += chunk)
            {
                int count = Math.Min(chunk, nq - start);
                deviceQueries.View.SubView(0, (long)count * D)
                    .CopyFromCPU(stream, queries.Slice(start * D, count * D).ToArray());

                _distanceKernel(stream, new Index2D(count, nlist),
                    deviceQueries.View, _centroids!.View, coarseDistances.View, D, nlist, coarseMetric);
                _selectKernel(stream, new Index1D(count),
                    coarseDistances.View, probeScores.View, probeLists.View, deviceMask.View,
                    nlist, nprobe, coarseMetric, count, 0);

                var args = new IvfKernelArgs
                {
                    D = D,
                    Nprobe = nprobe,
                    K = k,
                    Metric = metric,
                    Count = count,
                    UseMask = selector is null ? 0 : 1,
                };
                var lists = new ScanViews(
                    deviceQueries.View, _centroids.View, probeLists.View, _ids!.View, _offsets!.View, _sizes!.View,
                    deviceMask.View, partialDistances.View, partialLabels.View);
                Scan(stream, args, lists);

                _mergeKernel(stream, new Index1D(count),
                    partialDistances.View, partialLabels.View, outDistances.View, outLabels.View,
                    candidates, k, metric, count);

                stream.Synchronize();
                outDistances.View.SubView(0, (long)count * k).CopyToCPU(stream, hostDistances.AsSpan(0, count * k));
                outLabels.View.SubView(0, (long)count * k).CopyToCPU(stream, hostLabels.AsSpan(0, count * k));
                stream.Synchronize();

                hostDistances.AsSpan(0, count * k).CopyTo(distances.Slice(start * k, count * k));
                hostLabels.AsSpan(0, count * k).CopyTo(labels.Slice(start * k, count * k));
            }
        }
        finally
        {
            EndScratch();
        }
    }

    /// <summary>One membership byte per stored entry, in the same CSR order as the uploaded codes.</summary>
    private MemoryBuffer1D<byte, Stride1D.Dense> UploadMask(IDSelector selector)
    {
        var mask = new byte[Math.Max(1, _uploadedEntries)];
        for (int list = 0; list < _hostSizes.Length; list++)
        {
            int size = _hostSizes[list];
            if (size == 0 || !selector.MayIntersect(_listMin[list], _listMax[list])) continue;
            long offset = _hostOffsets[list];
            for (long e = offset; e < offset + size; e++)
                mask[e] = selector.IsMember(_hostIds[e]) ? (byte)1 : (byte)0;
        }
        return Resources.Accelerator.Allocate1D(mask);
    }

    /// <summary>Device views every scan kernel reads, gathered so the subclass hook stays small.</summary>
    private protected readonly record struct ScanViews(
        ArrayView<float> Queries,
        ArrayView<float> Centroids,
        ArrayView<long> ProbeLists,
        ArrayView<long> Ids,
        ArrayView<long> Offsets,
        ArrayView<int> Sizes,
        ArrayView<byte> Mask,
        ArrayView<float> PartialDistances,
        ArrayView<long> PartialLabels);

    /// <summary>Extra device bytes one query needs during the scan (lookup tables, for IVFPQ).</summary>
    private protected virtual long ScratchBytesPerQuery(int nprobe) => 0;

    /// <summary>Allocates per-search scratch sized for <paramref name="chunk"/> queries.</summary>
    private protected virtual void BeginScratch(Accelerator accelerator, int chunk, int nprobe) { }

    /// <summary>Frees what <see cref="BeginScratch"/> allocated.</summary>
    private protected virtual void EndScratch() { }

    /// <summary>
    /// Launches the scan for one chunk: one thread per (query, probe), each writing k partial
    /// results at <c>(q * nprobe + p) * k</c> in <see cref="ScanViews.PartialDistances"/>.
    /// </summary>
    private protected abstract void Scan(AcceleratorStream stream, IvfKernelArgs args, ScanViews views);

    // ------------------------------------------------------------ Copies

    /// <summary>
    /// An independent copy of a CPU IVF index. A trained index round-trips through
    /// <see cref="IndexIO"/>, which carries the quantizer, codebooks and lists exactly; an
    /// untrained one has nothing to carry but its shape, so <paramref name="fresh"/> builds it.
    /// </summary>
    private protected static T CopyOf<T>(T index, Func<T> fresh) where T : IndexIVF
    {
        T copy = index.IsTrained ? (T)IndexIO.Deserialize(IndexIO.Serialize(index)) : fresh();
        copy.Nprobe = index.Nprobe;
        copy.ClusteringParameters = index.ClusteringParameters;
        return copy;
    }

    /// <summary>A copy of the wrapped CPU index taken under the index lock, for <c>ToCpu</c>.</summary>
    private protected T CopyCpu<T>(Func<T, T> fresh) where T : IndexIVF
    {
        lock (_gate)
        {
            var cpu = (T)Cpu;
            return CopyOf(cpu, () => fresh(cpu));
        }
    }

    // ------------------------------------------------------------ Utility

    /// <summary>Host copy plus device copy of the lists.</summary>
    public override long MemoryUsage => Cpu.MemoryUsage * 2;

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {MetricType.ToShortString()}, device={Resources.DeviceName})";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDevice();
        }
        GC.SuppressFinalize(this);
    }
}
