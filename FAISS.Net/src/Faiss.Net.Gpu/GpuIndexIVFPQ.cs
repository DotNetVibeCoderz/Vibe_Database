using ILGPU;
using ILGPU.Runtime;

namespace Faiss.Net.Gpu;

/// <summary>
/// Inverted-file search over product-quantized codes, executed on the GPU — FAISS's
/// <c>GpuIndexIVFPQ</c>, and a drop-in replacement for <see cref="IndexIVFPQ"/>.
/// <para>
/// Training and encoding run on a private <see cref="IndexIVFPQ"/>, so the codes are the CPU
/// index's codes byte for byte. Search computes the same asymmetric (ADC) lookup tables the CPU
/// index does: for L2 the codes are residuals, so each (query, probed list) pair gets its own table
/// built from <c>query - centroid</c>; for inner product the codes encode raw vectors and the table
/// is built from the query itself. A table kernel writes one table per (query, probe) into global
/// memory — one thread per table entry — and the scan kernel then sums <c>m</c> table lookups per
/// code, one thread per (query, probe).
/// </para>
/// <para>
/// Only 8-bit sub-codes are supported on the device (<c>nbits == 8</c>, so <c>ksub = 256</c> and
/// every sub-code is one byte). That is the overwhelmingly common setting and the one FAISS's own
/// GPU IVFPQ is built around; other widths throw <see cref="NotSupportedException"/> at construction.
/// The CPU index's <see cref="IndexIVFPQ.UsePrecomputedTable"/> is not used here: the tables are
/// rebuilt per probe on the device, which is mathematically the same distance.
/// </para>
/// </summary>
public sealed class GpuIndexIVFPQ : GpuIndexIVF
{
    private MemoryBuffer1D<byte, Stride1D.Dense>? _codes;
    private MemoryBuffer1D<float, Stride1D.Dense>? _codebooks;
    private MemoryBuffer1D<float, Stride1D.Dense>? _tables;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<long>, ArrayView<float>, IvfKernelArgs> _tableKernel;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<byte>, ArrayView<long>,
        ArrayView<long>, ArrayView<long>, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<long>,
        IvfKernelArgs> _scanKernel;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells.</param>
    /// <param name="m">Sub-quantizers per vector; must divide <paramref name="dimension"/>.</param>
    /// <param name="nbits">Bits per sub-code. Only 8 is supported on the GPU.</param>
    /// <param name="metric">L2 or inner product.</param>
    /// <param name="resources">Accelerator to run on; <see cref="StandardGpuResources.Default"/> when null.</param>
    /// <exception cref="NotSupportedException"><paramref name="nbits"/> is not 8.</exception>
    public GpuIndexIVFPQ(int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2,
        StandardGpuResources? resources = null)
        : this(new IndexIVFPQ(dimension, nlist, m, RequireEightBits(nbits), metric), resources)
    {
    }

    private GpuIndexIVFPQ(IndexIVFPQ cpu, StandardGpuResources? resources)
        : base(cpu, resources)
    {
        var accelerator = Resources.Accelerator;
        _tableKernel = accelerator.LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<long>, ArrayView<float>, IvfKernelArgs>(TableKernel);
        _scanKernel = accelerator.LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<byte>, ArrayView<long>,
            ArrayView<long>, ArrayView<long>, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<long>,
            IvfKernelArgs>(ScanKernel);
    }

    private static int RequireEightBits(int nbits) => nbits == 8
        ? nbits
        : throw new NotSupportedException(
            $"GpuIndexIVFPQ supports nbits = 8 only (got {nbits}); use the CPU IndexIVFPQ for other code widths.");

    /// <summary>The product quantizer, exposed like <c>index.pq</c> in Python. Treat it as read-only.</summary>
    public ProductQuantizer Pq => ((IndexIVFPQ)Cpu).Pq;

    /// <summary>
    /// Copies a CPU index onto the GPU — the <c>index_cpu_to_gpu</c> direction. The copy is
    /// independent: later changes to <paramref name="index"/> are not seen by the GPU index.
    /// </summary>
    /// <exception cref="NotSupportedException">The index uses sub-codes other than 8 bits.</exception>
    public static GpuIndexIVFPQ FromCpu(IndexIVFPQ index, StandardGpuResources? resources = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        RequireEightBits(index.Pq.Nbits);
        return new GpuIndexIVFPQ(CopyOf(index, () => Fresh(index)), resources);
    }

    /// <summary>Copies this index back to an independent CPU <see cref="IndexIVFPQ"/> — the <c>index_gpu_to_cpu</c> direction.</summary>
    public IndexIVFPQ ToCpu() => CopyCpu<IndexIVFPQ>(Fresh);

    private static IndexIVFPQ Fresh(IndexIVFPQ index)
    {
        var copy = new IndexIVFPQ(index.D, index.Nlist, index.Pq.M, index.Pq.Nbits, index.MetricType);
        copy.Pq.ClusteringParameters = index.Pq.ClusteringParameters;
        return copy;
    }

    private protected override void UploadCodes(Accelerator accelerator, byte[] codes)
    {
        _codes = accelerator.Allocate1D(codes);
        _codebooks = accelerator.Allocate1D(Pq.Centroids);
    }

    private protected override void ReleaseCodes()
    {
        _codes?.Dispose();
        _codebooks?.Dispose();
        _codes = null;
        _codebooks = null;
    }

    private protected override long ScratchBytesPerQuery(int nprobe) =>
        (long)nprobe * Pq.DistanceTableSize * sizeof(float);

    private protected override void BeginScratch(Accelerator accelerator, int chunk, int nprobe) =>
        _tables = accelerator.Allocate1D<float>((long)chunk * nprobe * Pq.DistanceTableSize);

    private protected override void EndScratch()
    {
        _tables?.Dispose();
        _tables = null;
    }

    private protected override void Scan(AcceleratorStream stream, IvfKernelArgs args, ScanViews views)
    {
        var pq = Pq;
        args.M = pq.M;
        args.Ksub = pq.Ksub;
        args.Dsub = pq.Dsub;
        args.ByResidual = Cpu.ByResidual ? 1 : 0;

        _tableKernel(stream, new Index2D(args.Count * args.Nprobe, pq.DistanceTableSize),
            views.Queries, views.Centroids, _codebooks!.View, views.ProbeLists, _tables!.View, args);
        _scanKernel(stream, new Index2D(args.Count, args.Nprobe),
            _tables.View, _codes!.View, views.ProbeLists, views.Ids, views.Offsets, views.Sizes, views.Mask,
            views.PartialDistances, views.PartialLabels, args);
    }

    /// <summary>
    /// One thread per (query-probe pair, table entry): the contribution of codeword <c>c</c> of
    /// sub-quantizer <c>s</c>, written at <c>[pair][s * ksub + c]</c>. With residual coding the
    /// query is shifted by the probed centroid first, exactly as <see cref="IndexIVFPQ"/> does, so
    /// the table lookups add up to a distance in the original space.
    /// </summary>
    private static void TableKernel(
        Index2D index, ArrayView<float> queries, ArrayView<float> centroids, ArrayView<float> codebooks,
        ArrayView<long> probeLists, ArrayView<float> tables, IvfKernelArgs args)
    {
        int pair = index.X;
        int entry = index.Y;
        int tableSize = args.M * args.Ksub;
        if (pair >= args.Count * args.Nprobe || entry >= tableSize) return;

        int q = pair / args.Nprobe;
        int sub = entry / args.Ksub;
        long list = probeLists[pair];
        bool residual = args.ByResidual != 0 && list >= 0;

        long queryOffset = (long)q * args.D + (long)sub * args.Dsub;
        long centroidOffset = list * args.D + (long)sub * args.Dsub;
        long bookOffset = (long)entry * args.Dsub;
        float sum = 0f;

        for (int i = 0; i < args.Dsub; i++)
        {
            float x = queries[queryOffset + i];
            if (residual) x -= centroids[centroidOffset + i];
            float y = codebooks[bookOffset + i];
            if (args.Metric == 1)
            {
                sum += x * y;
            }
            else
            {
                float diff = x - y;
                sum += diff * diff;
            }
        }

        tables[(long)pair * tableSize + entry] = sum;
    }

    /// <summary>
    /// One thread per (query, probe): sums <c>m</c> table lookups per code in the probed list,
    /// keeping a k-slot partial result. Entries are walked in list order, as on the CPU.
    /// </summary>
    private static void ScanKernel(
        Index2D index, ArrayView<float> tables, ArrayView<byte> codes, ArrayView<long> probeLists,
        ArrayView<long> ids, ArrayView<long> offsets, ArrayView<int> sizes, ArrayView<byte> mask,
        ArrayView<float> partialDistances, ArrayView<long> partialLabels, IvfKernelArgs args)
    {
        int q = index.X;
        int p = index.Y;
        if (q >= args.Count || p >= args.Nprobe) return;

        long probe = (long)q * args.Nprobe + p;
        long slot = probe * args.K;
        bool similarity = args.Metric == 1;
        GpuKernels.Clear(partialDistances, partialLabels, slot, args.K, similarity);

        long list = probeLists[probe];
        if (list < 0) return;
        long start = offsets[list];
        int size = sizes[list];
        long tableOffset = probe * args.M * args.Ksub;

        for (int i = 0; i < size; i++)
        {
            long entry = start + i;
            if (args.UseMask != 0 && mask[entry] == 0) continue;

            long codeOffset = entry * args.M;
            float sum = 0f;
            for (int sub = 0; sub < args.M; sub++)
                sum += tables[tableOffset + (long)sub * args.Ksub + codes[codeOffset + sub]];
            GpuKernels.Insert(partialDistances, partialLabels, slot, args.K, sum, ids[entry], similarity);
        }
    }

    public override string Describe() =>
        $"GpuIndexIVFPQ(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {Pq}, " +
        $"{(Cpu.ByResidual ? "residual" : "direct")}, {MetricType.ToShortString()}, device={Resources.DeviceName})";
}
