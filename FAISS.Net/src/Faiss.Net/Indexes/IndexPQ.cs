using System.Buffers;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Flat index over product-quantized codes: still an exhaustive scan, but of <c>m</c>-byte codes
/// instead of <c>d</c> floats.
/// <para>
/// Every vector is visited, so recall loss comes only from quantization, never from pruning — which
/// makes this the index to reach for when the dataset must shrink but the candidate set must not.
/// With <c>m = 16, nbits = 8</c> on 128-dim data it is 32x smaller than <see cref="IndexFlat"/>, and
/// the scan is often <em>faster</em> in wall-clock terms despite doing the same amount of logical
/// work, because 16 bytes per vector streams through cache where 512 bytes does not.
/// </para>
/// </summary>
public class IndexPQ : Index
{
    private byte[] _codes = [];
    private int _count;

    /// <summary>The underlying quantizer; exposed like <c>index.pq</c> in Python.</summary>
    public ProductQuantizer Pq { get; private set; }

    /// <summary>Bytes per stored vector.</summary>
    public int CodeSize => Pq.CodeSize;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="m">Sub-quantizers; must divide <paramref name="dimension"/>. Higher m means better accuracy and a larger code.</param>
    /// <param name="nbits">Bits per sub-code; 8 is the default and fastest.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexPQ(int dimension, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(dimension, metric)
    {
        Pq = new ProductQuantizer(dimension, m, nbits);
        IsTrained = false;
    }

    internal IndexPQ(int dimension, MetricType metric) : base(dimension, metric)
    {
        Pq = new ProductQuantizer(dimension, 1, 8);
        IsTrained = false;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Raw codes, <c>ntotal * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan(0, _count * CodeSize);

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Pq.Train(x);
        IsTrained = true;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        EnsureCapacity(n);
        Pq.ComputeCodes(x, n, _codes.AsSpan(_count * CodeSize, n * CodeSize));
        _count += n;
        Ntotal = _count;
    }

    private void EnsureCapacity(int additional)
    {
        long needed = (long)(_count + additional) * CodeSize;
        if (needed <= _codes.Length) return;
        long grown = Math.Max(needed, _codes.Length + (_codes.Length >> 1));
        Array.Resize(ref _codes, (int)grown);
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
        fixed (byte* pcodes = _codes)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, cp = (nint)pcodes, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            if (nq == 1 || threads == 1 || (long)nq * _count < 100_000)
                for (int q = 0; q < nq; q++)
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k));
        }
    }

    /// <summary>Builds this query's ADC table once, then sweeps every code against it.</summary>
    private unsafe void ScanQuery(float* query, byte* codes, float* outDistances, long* outLabels, int k)
    {
        int tableSize = Pq.DistanceTableSize;
        float[] rented = ArrayPool<float>.Shared.Rent(tableSize);
        try
        {
            Pq.ComputeDistanceTable(new ReadOnlySpan<float>(query, D), rented.AsSpan(0, tableSize), MetricType);
            fixed (float* table = rented)
            {
                if (MetricType.IsSimilarity())
                    Sweep<DescendingOrder>(table, codes, outDistances, outLabels, k);
                else
                    Sweep<AscendingOrder>(table, codes, outDistances, outLabels, k);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private unsafe void Sweep<TOrder>(float* table, byte* codes, float* outDistances, long* outLabels, int k)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        int codeSize = CodeSize;
        for (int i = 0; i < _count; i++)
        {
            float score = Pq.DistanceFromTable(table, codes + (long)i * codeSize);
            if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, i);
        }
        heap.Finish();
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _count) throw new ArgumentOutOfRangeException(nameof(key));
        Pq.Decode(_codes.AsSpan((int)key * CodeSize, CodeSize), output);
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

    public override long MemoryUsage => _codes.Length + Pq.CodebookBytes;

    public override string Describe() =>
        $"IndexPQ(d={D}, ntotal={Ntotal}, {Pq}, {MetricType.ToShortString()})";

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.PQ;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        Pq.Write(writer);
        writer.Write(_count);
        writer.Write(_codes.AsSpan(0, _count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Pq = ProductQuantizer.Read(reader);
        _count = reader.ReadInt32();
        _codes = new byte[(long)_count * CodeSize];
        reader.ReadExactly(_codes);
        Ntotal = _count;
        IsTrained = Pq.IsTrained;
    }
}
