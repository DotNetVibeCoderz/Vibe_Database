using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net.Binary;

/// <summary>
/// Base class for indexes over packed binary codes. Mirrors <c>faiss.IndexBinary</c>: dimension is
/// counted in <em>bits</em> and must be a multiple of 8, vectors are passed as packed bytes, and
/// distances are integral Hamming distances (returned as floats so one result type serves the whole
/// library).
/// </summary>
public abstract class IndexBinary
{
    /// <summary>Dimension in bits.</summary>
    public int D { get; protected set; }

    /// <summary>Bytes per code, <c>d / 8</c>.</summary>
    public int CodeSize => D / 8;

    /// <summary>Number of indexed codes.</summary>
    public long Ntotal { get; protected set; }

    /// <summary>False until training has run, for indexes that need it.</summary>
    public bool IsTrained { get; protected set; } = true;

    /// <summary>Maximum worker threads; <c>0</c> means every core.</summary>
    public int Threads { get; set; }

    protected IndexBinary(int dimension)
    {
        if (dimension <= 0 || dimension % 8 != 0)
            throw new ArgumentException("Binary dimension must be a positive multiple of 8 bits.", nameof(dimension));
        D = dimension;
    }

    public virtual void Train(ReadOnlySpan<byte> x) { }

    /// <summary>Adds <c>x.Length / CodeSize</c> codes.</summary>
    public abstract void Add(ReadOnlySpan<byte> x);

    /// <summary>k nearest codes by Hamming distance.</summary>
    public SearchResult Search(ReadOnlySpan<byte> queries, int k)
    {
        int nq = queries.Length / CodeSize;
        var result = SearchResult.Allocate(nq, k);
        Search(queries, nq, k, result.Distances, result.Labels);
        return result;
    }

    public abstract void Search(ReadOnlySpan<byte> queries, int nq, int k, Span<float> distances, Span<long> labels);

    /// <summary>Every code within <paramref name="radius"/> bits of each query.</summary>
    public abstract RangeSearchResult RangeSearch(ReadOnlySpan<byte> queries, int radius);

    public abstract void Reset();

    /// <summary>Approximate resident bytes.</summary>
    public virtual long MemoryUsage => 0;

    public virtual string Describe() => $"{GetType().Name}(d={D} bits, ntotal={Ntotal})";

    public override string ToString() => Describe();

    /// <summary>
    /// One integer written into the file header, before the body, for parameters the constructor
    /// needs in order to exist at all (an IVF index cannot allocate its lists without nlist).
    /// </summary>
    protected internal virtual int SerializationParameter => 0;

    protected internal virtual IndexTypeCode TypeCode => throw new NotSupportedException();

    protected internal virtual void WriteBody(BinaryWriter writer) => throw new NotSupportedException();

    protected internal virtual void ReadBody(BinaryReader reader) => throw new NotSupportedException();
}

/// <summary>
/// Exhaustive Hamming search over packed binary codes.
/// <para>
/// The scan is XOR plus popcount per candidate, so it sustains billions of bit-comparisons per
/// second per core; a million 256-bit codes is 32 MB and scans in roughly a millisecond. Recall is
/// exact with respect to the binary codes — any loss happened when the vectors were binarized, not
/// here.
/// </para>
/// </summary>
public sealed class IndexBinaryFlat : IndexBinary
{
    private byte[] _codes = [];
    private int _count;

    public IndexBinaryFlat(int dimension) : base(dimension) { }

    /// <summary>Raw stored codes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan(0, _count * CodeSize);

    public override void Add(ReadOnlySpan<byte> x)
    {
        if (x.Length % CodeSize != 0)
            throw new ArgumentException($"Input length {x.Length} is not a multiple of the code size {CodeSize}.", nameof(x));
        int n = x.Length / CodeSize;
        if (n == 0) return;

        long needed = (long)(_count + n) * CodeSize;
        if (needed > _codes.Length)
            Array.Resize(ref _codes, (int)Math.Max(needed, _codes.Length + (_codes.Length >> 1)));

        x.CopyTo(_codes.AsSpan(_count * CodeSize));
        _count += n;
        Ntotal = _count;
    }

    public override unsafe void Search(ReadOnlySpan<byte> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (nq == 0) return;
        if (_count == 0)
        {
            distances.Fill(float.MaxValue);
            labels.Fill(-1);
            return;
        }

        // k is deliberately NOT clamped to ntotal: the caller sized its buffers for the k it asked
        // for, so the row stride must stay k. The heap pads the unused slots with -1, as FAISS does.
        fixed (byte* xq = queries)
        fixed (byte* xb = _codes)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, bp = (nint)xb, dp = (nint)pdis, lp = (nint)plab;
            int codeSize = CodeSize, count = _count;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            void Scan(int q)
            {
                byte* query = (byte*)qp + (long)q * codeSize;
                byte* database = (byte*)bp;
                var heap = new KnnHeap<AscendingOrder>(
                    new Span<float>((float*)dp + (long)q * k, k),
                    new Span<long>((long*)lp + (long)q * k, k));
                for (int i = 0; i < count; i++)
                {
                    float distance = HammingOps.Distance(query, database + (long)i * codeSize, codeSize);
                    if (distance < heap.WorstScore) heap.Push(distance, i);
                }
                heap.Finish();
            }

            if (threads == 1 || (long)nq * count < 200_000)
                for (int q = 0; q < nq; q++) Scan(q);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, Scan);
        }
    }

    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<byte> queries, int radius)
    {
        int nq = queries.Length / CodeSize;
        var perQuery = new List<(long Id, float Distance)>[nq];

        fixed (byte* xq = queries)
        fixed (byte* xb = _codes)
        {
            nint qp = (nint)xq, bp = (nint)xb;
            int codeSize = CodeSize, count = _count;

            Parallel.For(0, nq, q =>
            {
                byte* query = (byte*)qp + (long)q * codeSize;
                byte* database = (byte*)bp;
                var hits = new List<(long, float)>();
                for (int i = 0; i < count; i++)
                {
                    int distance = HammingOps.Distance(query, database + (long)i * codeSize, codeSize);
                    if (distance <= radius) hits.Add((i, distance));
                }
                perQuery[q] = hits;
            });
        }

        return Core.BruteForce.Flatten(perQuery);
    }

    /// <summary>Copies one stored code out.</summary>
    public void Reconstruct(long key, Span<byte> output)
    {
        if (key < 0 || key >= _count) throw new ArgumentOutOfRangeException(nameof(key));
        _codes.AsSpan((int)key * CodeSize, CodeSize).CopyTo(output);
    }

    public override void Reset()
    {
        _count = 0;
        Ntotal = 0;
    }

    public override long MemoryUsage => _codes.Length;

    public override string Describe() => $"IndexBinaryFlat(d={D} bits, ntotal={Ntotal}, {CodeSize}B/code)";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.BinaryFlat;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(_count);
        writer.Write(_codes.AsSpan(0, _count * CodeSize));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        _count = reader.ReadInt32();
        _codes = new byte[(long)_count * CodeSize];
        reader.ReadExactly(_codes);
        Ntotal = _count;
    }
}
