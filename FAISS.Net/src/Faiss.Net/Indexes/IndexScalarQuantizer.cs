using System.Buffers;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Flat index over scalar-quantized codes: an exhaustive scan with 4x (8-bit) or 8x (4-bit) less
/// memory traffic than <see cref="IndexFlat"/>.
/// <para>
/// Unlike <see cref="IndexPQ"/> this needs no clustering — training is a single min/max pass — and
/// 8-bit codes usually cost well under a point of recall. When an index has outgrown RAM but the
/// accuracy budget is tight, this is the first thing to try.
/// </para>
/// <para>
/// Each candidate is decoded into a small pooled buffer and compared with the usual SIMD kernels.
/// Decoding costs a few operations per dimension, but the scan reads a quarter of the bytes, so on
/// memory-bound workloads the two roughly cancel and the memory saving comes for free.
/// </para>
/// </summary>
public sealed class IndexScalarQuantizer : Index
{
    private CodeStore _codes;

    /// <summary>The underlying quantizer; exposed like <c>index.sq</c> in Python.</summary>
    public ScalarQuantizer Sq { get; private set; }

    /// <summary>Bytes per stored vector.</summary>
    public int CodeSize => Sq.CodeSize;

    public IndexScalarQuantizer(
        int dimension,
        ScalarQuantizerType type = ScalarQuantizerType.PerDimension8Bit,
        MetricType metric = MetricType.L2)
        : base(dimension, metric)
    {
        Sq = new ScalarQuantizer(dimension, type);
        _codes = new CodeStore(Sq.CodeSize);
        IsTrained = Sq.IsTrained;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Raw codes, <c>ntotal * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan();

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Sq.Train(x);
        IsTrained = true;
    }

    public override unsafe void Add(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        fixed (float* px = x)
        {
            nint xp = (nint)px;
            _codes.Append(n, codes => Sq.EncodeBatch(new ReadOnlySpan<float>((float*)xp, n * D), n, codes));
        }
        Ntotal = _codes.Count;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override unsafe void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        EnsureTrained();
        if (nq == 0) return;
        var (data, count) = _codes.Snapshot();
        if (count == 0)
        {
            distances[..(nq * k)].Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        var sel = parameters?.Sel;
        fixed (float* xq = queries)
        fixed (byte* pcodes = data)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, cp = (nint)pcodes, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            if (nq == 1 || threads == 1 || (long)nq * count < 50_000)
                for (int q = 0; q < nq; q++)
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, count, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k, sel);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, count, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k, sel));
        }
    }

    private unsafe void ScanQuery(float* query, byte* codes, int count, float* outDistances, long* outLabels, int k, IDSelector? sel)
    {
        float[] scratch = ArrayPool<float>.Shared.Rent(D);
        try
        {
            if (MetricType.IsSimilarity())
                Sweep<DescendingOrder>(query, scratch, codes, count, outDistances, outLabels, k, sel);
            else
                Sweep<AscendingOrder>(query, scratch, codes, count, outDistances, outLabels, k, sel);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private unsafe void Sweep<TOrder>(
        float* query, float[] scratch, byte* codes, int count, float* outDistances, long* outLabels, int k, IDSelector? sel)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        int codeSize = CodeSize;

        // Pinned once for the whole scan, not once per candidate: see ScalarQuantizer.DecodeUnchecked.
        fixed (float* pdecoded = scratch)
        fixed (float* poffsets = Sq.Offsets)
        fixed (float* psteps = Sq.Steps)
        {
            for (int i = 0; i < count; i++)
            {
                if (sel is not null && !sel.IsMember(i)) continue;
                Sq.DecodeUnchecked(codes + (long)i * codeSize, pdecoded, poffsets, psteps);
                float score = VectorOps.Distance(query, pdecoded, D, MetricType);
                if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, i);
            }
        }
        heap.Finish();
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        Sq.Decode(_codes[(int)key], output);
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        int removed = _codes.Compact(i => !predicate(i));
        Ntotal = _codes.Count;
        return removed;
    }

    public override void Reset()
    {
        _codes.Clear();
        Ntotal = 0;
    }

    public override long MemoryUsage => _codes.MemoryUsage;

    public override string Describe() =>
        $"IndexScalarQuantizer(d={D}, ntotal={Ntotal}, {Sq}, {MetricType.ToShortString()})";

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.ScalarQuantizer;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        Sq.Write(writer);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Sq = ScalarQuantizer.Read(reader);
        int count = reader.ReadInt32();
        var data = new byte[(long)count * CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(Sq.CodeSize);
        _codes.Load(data, count);
        Ntotal = count;
        IsTrained = Sq.IsTrained;
    }
}
