using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace Faiss.Net.Gpu;

/// <summary>
/// Inverted-file search with full-precision vectors, executed on the GPU — FAISS's
/// <c>GpuIndexIVFFlat</c>, and a drop-in replacement for <see cref="IndexIVFFlat"/>.
/// <para>
/// Training, adding and removal run on a private <see cref="IndexIVFFlat"/>, so the lists hold
/// exactly what the CPU index would hold. Search runs on the device: coarse quantization, then one
/// thread per (query, probed list) computing exact distances to every entry of that list, then a
/// per-query merge. Because the distances are exact and the candidate order matches the CPU scan,
/// the results agree with the CPU index up to floating-point summation order.
/// </para>
/// </summary>
public sealed class GpuIndexIVFFlat : GpuIndexIVF
{
    private MemoryBuffer1D<float, Stride1D.Dense>? _vectors;

    private readonly Action<AcceleratorStream, Index2D, ArrayView<float>, ArrayView<float>, ArrayView<long>,
        ArrayView<long>, ArrayView<long>, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<long>,
        IvfKernelArgs> _scanKernel;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells; a good starting point is <c>sqrt(expected ntotal)</c>.</param>
    /// <param name="metric">L2 or inner product.</param>
    /// <param name="resources">Accelerator to run on; <see cref="StandardGpuResources.Default"/> when null.</param>
    public GpuIndexIVFFlat(int dimension, int nlist, MetricType metric = MetricType.L2, StandardGpuResources? resources = null)
        : this(new IndexIVFFlat(dimension, nlist, metric), resources)
    {
    }

    private GpuIndexIVFFlat(IndexIVFFlat cpu, StandardGpuResources? resources)
        : base(cpu, resources)
    {
        _scanKernel = Resources.Accelerator.LoadAutoGroupedKernel<Index2D, ArrayView<float>, ArrayView<float>,
            ArrayView<long>, ArrayView<long>, ArrayView<long>, ArrayView<int>, ArrayView<byte>, ArrayView<float>,
            ArrayView<long>, IvfKernelArgs>(ScanKernel);
    }

    /// <summary>
    /// Copies a CPU index onto the GPU — the <c>index_cpu_to_gpu</c> direction. The copy is
    /// independent: later changes to <paramref name="index"/> are not seen by the GPU index.
    /// </summary>
    public static GpuIndexIVFFlat FromCpu(IndexIVFFlat index, StandardGpuResources? resources = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new GpuIndexIVFFlat(CopyOf(index, () => Fresh(index)), resources);
    }

    /// <summary>Copies this index back to an independent CPU <see cref="IndexIVFFlat"/> — the <c>index_gpu_to_cpu</c> direction.</summary>
    public IndexIVFFlat ToCpu() => CopyCpu<IndexIVFFlat>(Fresh);

    private static IndexIVFFlat Fresh(IndexIVFFlat index) => new(index.D, index.Nlist, index.MetricType);

    private protected override void UploadCodes(Accelerator accelerator, byte[] codes) =>
        _vectors = accelerator.Allocate1D(MemoryMarshal.Cast<byte, float>(codes).ToArray());

    private protected override void ReleaseCodes()
    {
        _vectors?.Dispose();
        _vectors = null;
    }

    private protected override void Scan(AcceleratorStream stream, IvfKernelArgs args, ScanViews views) =>
        _scanKernel(stream, new Index2D(args.Count, args.Nprobe),
            views.Queries, _vectors!.View, views.ProbeLists, views.Ids, views.Offsets, views.Sizes, views.Mask,
            views.PartialDistances, views.PartialLabels, args);

    /// <summary>
    /// One thread per (query, probe): exact distance from the query to every entry of the probed
    /// list, kept as a k-slot partial result. Entries are walked in list order, the order the CPU
    /// scan uses, so ties resolve the same way.
    /// </summary>
    private static void ScanKernel(
        Index2D index, ArrayView<float> queries, ArrayView<float> vectors, ArrayView<long> probeLists,
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
        long queryOffset = (long)q * args.D;

        for (int i = 0; i < size; i++)
        {
            long entry = start + i;
            if (args.UseMask != 0 && mask[entry] == 0) continue;

            long vectorOffset = entry * args.D;
            float sum = 0f;
            if (similarity)
            {
                for (int j = 0; j < args.D; j++) sum += queries[queryOffset + j] * vectors[vectorOffset + j];
            }
            else
            {
                for (int j = 0; j < args.D; j++)
                {
                    float diff = queries[queryOffset + j] - vectors[vectorOffset + j];
                    sum += diff * diff;
                }
            }
            GpuKernels.Insert(partialDistances, partialLabels, slot, args.K, sum, ids[entry], similarity);
        }
    }

    public override string Describe() =>
        $"GpuIndexIVFFlat(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {MetricType.ToShortString()}, device={Resources.DeviceName})";
}
