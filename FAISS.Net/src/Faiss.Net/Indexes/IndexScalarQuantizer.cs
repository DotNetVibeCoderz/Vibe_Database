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
    private byte[] _codes = [];
    private int _count;

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
        IsTrained = Sq.IsTrained;
    }

    public override bool SupportsReconstruct => true;

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Sq.Train(x);
        IsTrained = true;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        long needed = (long)(_count + n) * CodeSize;
        if (needed > _codes.Length)
            Array.Resize(ref _codes, (int)Math.Max(needed, _codes.Length + (_codes.Length >> 1)));

        Sq.EncodeBatch(x, n, _codes.AsSpan(_count * CodeSize, n * CodeSize));
        _count += n;
        Ntotal = _count;
    }

    public override unsafe void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        EnsureTrained();
        if (nq == 0) return;
        if (_count == 0)
        {
            distances.Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels.Fill(-1);
            return;
        }

        fixed (float* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            if (nq == 1 || threads == 1 || (long)nq * _count < 50_000)
                for (int q = 0; q < nq; q++)
                    ScanQuery((float*)qp + (long)q * D, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    ScanQuery((float*)qp + (long)q * D, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k));
        }
    }

    private unsafe void ScanQuery(float* query, float* outDistances, long* outLabels, int k)
    {
        float[] scratch = ArrayPool<float>.Shared.Rent(D);
        try
        {
            if (MetricType.IsSimilarity())
                Sweep<DescendingOrder>(query, scratch, outDistances, outLabels, k);
            else
                Sweep<AscendingOrder>(query, scratch, outDistances, outLabels, k);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private unsafe void Sweep<TOrder>(float* query, float[] scratch, float* outDistances, long* outLabels, int k)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        int codeSize = CodeSize;

        // Pinned once for the whole scan, not once per candidate: see ScalarQuantizer.DecodeUnchecked.
        fixed (float* pdecoded = scratch)
        fixed (byte* pcodes = _codes)
        fixed (float* poffsets = Sq.Offsets)
        fixed (float* psteps = Sq.Steps)
        {
            for (int i = 0; i < _count; i++)
            {
                Sq.DecodeUnchecked(pcodes + (long)i * codeSize, pdecoded, poffsets, psteps);
                float score = VectorOps.Distance(query, pdecoded, D, MetricType);
                if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, i);
            }
        }
        heap.Finish();
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _count) throw new ArgumentOutOfRangeException(nameof(key));
        Sq.Decode(_codes.AsSpan((int)key * CodeSize, CodeSize), output);
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        int write = 0;
        for (int read = 0; read < _count; read++)
        {
            if (predicate(read)) continue;
            if (write != read)
                _codes.AsSpan(read * CodeSize, CodeSize).CopyTo(_codes.AsSpan(write * CodeSize, CodeSize));
            write++;
        }
        int removed = _count - write;
        _count = write;
        Ntotal = _count;
        return removed;
    }

    public override void Reset()
    {
        _count = 0;
        Ntotal = 0;
    }

    public override long MemoryUsage => _codes.Length;

    public override string Describe() =>
        $"IndexScalarQuantizer(d={D}, ntotal={Ntotal}, {Sq}, {MetricType.ToShortString()})";

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.ScalarQuantizer;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        Sq.Write(writer);
        writer.Write(_count);
        writer.Write(_codes.AsSpan(0, _count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Sq = ScalarQuantizer.Read(reader);
        _count = reader.ReadInt32();
        _codes = new byte[(long)_count * CodeSize];
        reader.ReadExactly(_codes);
        Ntotal = _count;
        IsTrained = Sq.IsTrained;
    }
}
