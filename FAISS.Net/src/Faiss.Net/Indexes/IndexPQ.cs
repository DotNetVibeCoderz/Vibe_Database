using System.Buffers;
using Faiss.Net.Core;
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
    private CodeStore _codes;

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
        _codes = new CodeStore(Pq.CodeSize);
        IsTrained = false;
    }

    internal IndexPQ(int dimension, MetricType metric) : this(dimension, 1, 8, metric) { }

    public override bool SupportsReconstruct => true;

    /// <summary>Raw codes, <c>ntotal * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan();

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Pq.Train(x);
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
            _codes.Append(n, codes => Pq.ComputeCodes(new ReadOnlySpan<float>((float*)xp, n * D), n, codes));
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

            if (nq == 1 || threads == 1 || (long)nq * count < 100_000)
                for (int q = 0; q < nq; q++)
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, count, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k, sel);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    ScanQuery((float*)qp + (long)q * D, (byte*)cp, count, (float*)dp + (long)q * k, (long*)lp + (long)q * k, k, sel));
        }
    }

    /// <summary>Builds this query's ADC table once, then sweeps every code against it.</summary>
    private unsafe void ScanQuery(float* query, byte* codes, int count, float* outDistances, long* outLabels, int k, IDSelector? sel)
    {
        int tableSize = Pq.DistanceTableSize;
        float[] rented = ArrayPool<float>.Shared.Rent(tableSize);
        try
        {
            Pq.ComputeDistanceTable(new ReadOnlySpan<float>(query, D), rented.AsSpan(0, tableSize), MetricType);
            fixed (float* table = rented)
            {
                if (MetricType.IsSimilarity())
                    Sweep<DescendingOrder>(table, codes, count, outDistances, outLabels, k, sel);
                else
                    Sweep<AscendingOrder>(table, codes, count, outDistances, outLabels, k, sel);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private unsafe void Sweep<TOrder>(float* table, byte* codes, int count, float* outDistances, long* outLabels, int k, IDSelector? sel)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        int codeSize = CodeSize;
        if (sel is null)
        {
            for (int i = 0; i < count; i++)
            {
                float score = Pq.DistanceFromTable(table, codes + (long)i * codeSize);
                if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, i);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (!sel.IsMember(i)) continue;
                float score = Pq.DistanceFromTable(table, codes + (long)i * codeSize);
                if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, i);
            }
        }
        heap.Finish();
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        Pq.Decode(_codes[(int)key], output);
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

    public override long MemoryUsage => _codes.MemoryUsage + Pq.CodebookBytes;

    public override string Describe() =>
        $"IndexPQ(d={D}, ntotal={Ntotal}, {Pq}, {MetricType.ToShortString()})";

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.PQ;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        Pq.Write(writer);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Pq = ProductQuantizer.Read(reader);
        int count = reader.ReadInt32();
        var data = new byte[(long)count * CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(Pq.CodeSize);
        _codes.Load(data, count);
        Ntotal = count;
        IsTrained = Pq.IsTrained;
    }
}
