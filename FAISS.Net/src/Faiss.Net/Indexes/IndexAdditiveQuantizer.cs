using System.Buffers;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Flat index over additive-quantizer codes — FAISS's <c>IndexAdditiveQuantizer</c>, the shared base
/// of <see cref="IndexResidualQuantizer"/> and <see cref="IndexLocalSearchQuantizer"/>.
/// <para>
/// Like <see cref="IndexPQ"/> it scans every code, so recall is limited only by quantization error,
/// and the scan is one lookup table per query plus <c>M</c> lookups per code. For L2 each code also
/// carries the squared norm of its reconstruction (4 bytes, <see cref="AdditiveQuantizerSearchType.NormFloat"/>):
/// an additive reconstruction's norm involves every pair of codewords, so it cannot be read from a
/// per-query table the way PQ's per-slice distances can. Inner-product indexes store codes only.
/// </para>
/// </summary>
public abstract class IndexAdditiveQuantizer : Index
{
    private CodeStore _codes;

    /// <summary>The quantizer; exposed like <c>index.aq</c> in Python.</summary>
    public AdditiveQuantizer Aq { get; private set; }

    /// <summary>Bytes per stored vector, including the norm for L2.</summary>
    public int CodeSize => Aq.CodeSize;

    protected IndexAdditiveQuantizer(int dimension, AdditiveQuantizer aq, MetricType metric)
        : base(dimension, metric)
    {
        ArgumentNullException.ThrowIfNull(aq);
        if (metric is not (MetricType.L2 or MetricType.InnerProduct))
            throw new ArgumentException($"{GetType().Name} supports L2 and inner product, not {metric}.", nameof(metric));
        if (aq.D != dimension)
            throw new ArgumentException($"Quantizer dimension {aq.D} does not match index dimension {dimension}.");
        Aq = aq;
        _codes = new CodeStore(aq.CodeSize);
        IsTrained = false;
    }

    /// <summary>The search type an index with this metric needs: a stored norm for L2, none for inner product.</summary>
    protected internal static AdditiveQuantizerSearchType SearchTypeFor(MetricType metric) =>
        metric == MetricType.L2 ? AdditiveQuantizerSearchType.NormFloat : AdditiveQuantizerSearchType.LutNonorm;

    public override bool SupportsReconstruct => true;

    /// <summary>Raw codes, <c>ntotal * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan();

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Aq.Train(x);
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
            _codes.Append(n, codes => Aq.ComputeCodes(new ReadOnlySpan<float>((float*)xp, n * D), n, codes));
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

    /// <summary>Codes scored per block: small enough to stay in L1, large enough to amortize the call.</summary>
    private const int ScoreBlock = 512;

    /// <summary>Builds this query's lookup table once, then sweeps every code against it in blocks.</summary>
    private unsafe void ScanQuery(float* query, byte* codes, int count, float* outDistances, long* outLabels, int k, IDSelector? sel)
    {
        int tableSize = Aq.LookupTableSize;
        float[] table = ArrayPool<float>.Shared.Rent(tableSize);
        float[] scores = ArrayPool<float>.Shared.Rent(ScoreBlock);
        try
        {
            var q = new ReadOnlySpan<float>(query, D);
            Aq.ComputeLookupTable(q, table);
            bool l2 = MetricType == MetricType.L2;
            float bias = l2 ? VectorOps.Norm2Sqr(query, D) : 0f;
            if (l2)
                Sweep<AscendingOrder>(table, scores, bias, true, codes, count, outDistances, outLabels, k, sel);
            else
                Sweep<DescendingOrder>(table, scores, bias, false, codes, count, outDistances, outLabels, k, sel);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scores);
            ArrayPool<float>.Shared.Return(table);
        }
    }

    private unsafe void Sweep<TOrder>(
        float[] table, float[] scores, float bias, bool l2, byte* codes, int count,
        float* outDistances, long* outLabels, int k, IDSelector? sel)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        int codeSize = CodeSize;
        var lut = table.AsSpan(0, Aq.LookupTableSize);
        for (int start = 0; start < count; start += ScoreBlock)
        {
            int block = Math.Min(ScoreBlock, count - start);
            var slice = scores.AsSpan(0, block);
            Aq.ScoreCodes(lut, new ReadOnlySpan<byte>(codes + (long)start * codeSize, block * codeSize), slice, bias, l2);
            if (sel is null)
            {
                for (int i = 0; i < block; i++)
                    if (TOrder.Better(slice[i], heap.WorstScore)) heap.Push(slice[i], start + i);
            }
            else
            {
                for (int i = 0; i < block; i++)
                    if (TOrder.Better(slice[i], heap.WorstScore) && sel.IsMember(start + i)) heap.Push(slice[i], start + i);
            }
        }
        heap.Finish();
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        Aq.Decode(_codes[(int)key], output);
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

    public override long MemoryUsage => _codes.MemoryUsage + Aq.CodebookBytes;

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, {Aq}, {MetricType.ToShortString()})";

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>, counting the stored norm.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    // -------------------------------------------------------- Serialization

    /// <summary>Reads the concrete quantizer type this index holds.</summary>
    protected abstract AdditiveQuantizer ReadQuantizer(BinaryReader reader);

    protected internal override void WriteBody(BinaryWriter writer)
    {
        Aq.Write(writer);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Aq = ReadQuantizer(reader);
        int count = reader.ReadInt32();
        var data = new byte[(long)count * CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(Aq.CodeSize);
        _codes.Load(data, count);
        Ntotal = count;
        IsTrained = Aq.IsTrained;
    }
}

/// <summary>
/// Flat index over residual-quantizer codes — FAISS's <c>faiss.IndexResidualQuantizer(d, M, nbits)</c>.
/// See <see cref="ResidualQuantizer"/> for the encoding and <see cref="IndexAdditiveQuantizer"/> for search.
/// </summary>
public sealed class IndexResidualQuantizer : IndexAdditiveQuantizer
{
    /// <summary>The quantizer; exposed like <c>index.rq</c> in Python.</summary>
    public ResidualQuantizer Rq => (ResidualQuantizer)Aq;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="m">Number of stages; the code is <c>m * nbits</c> bits, plus a 4-byte norm for L2.</param>
    /// <param name="nbits">Bits per stage; 8 is the default.</param>
    /// <param name="metric">L2 or inner product.</param>
    public IndexResidualQuantizer(int dimension, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(dimension, new ResidualQuantizer(dimension, m, nbits, SearchTypeFor(metric)), metric)
    {
    }

    protected override AdditiveQuantizer ReadQuantizer(BinaryReader reader) => ResidualQuantizer.Read(reader);

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.ResidualQuantizer;
}

/// <summary>
/// Flat index over local-search-quantizer codes — FAISS's <c>faiss.IndexLocalSearchQuantizer(d, M, nbits)</c>.
/// See <see cref="LocalSearchQuantizer"/> for the encoding and <see cref="IndexAdditiveQuantizer"/> for search.
/// </summary>
public sealed class IndexLocalSearchQuantizer : IndexAdditiveQuantizer
{
    /// <summary>The quantizer; exposed like <c>index.lsq</c> in Python.</summary>
    public LocalSearchQuantizer Lsq => (LocalSearchQuantizer)Aq;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="m">Number of codebooks; the code is <c>m * nbits</c> bits, plus a 4-byte norm for L2.</param>
    /// <param name="nbits">Bits per codebook index; 8 is the default.</param>
    /// <param name="metric">L2 or inner product.</param>
    public IndexLocalSearchQuantizer(int dimension, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(dimension, new LocalSearchQuantizer(dimension, m, nbits, SearchTypeFor(metric)), metric)
    {
    }

    protected override AdditiveQuantizer ReadQuantizer(BinaryReader reader) => LocalSearchQuantizer.Read(reader);

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.LocalSearchQuantizer;
}
