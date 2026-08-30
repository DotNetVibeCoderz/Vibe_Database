using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Exhaustive, exact index: every query is compared against every stored vector.
/// <para>
/// Recall is 100% by construction, which makes this the reference every approximate index is
/// measured against, and the right choice outright up to roughly a few hundred thousand vectors.
/// Cost is linear in <c>ntotal * d</c>, so search time grows with the database while memory is
/// exactly <c>4 * ntotal * d</c> bytes with no overhead.
/// </para>
/// <para>Ids are positions. Removing a vector renumbers everything after it, as in FAISS; wrap in
/// <see cref="IndexIDMap"/> when ids must be stable.</para>
/// </summary>
public class IndexFlat : Index
{
    private readonly VectorStore _store;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="metric">Distance metric; defaults to squared L2.</param>
    public IndexFlat(int dimension, MetricType metric = MetricType.L2) : base(dimension, metric)
    {
        _store = new VectorStore(dimension);
        IsTrained = true;
    }

    /// <summary>Raw stored vectors, <c>ntotal * d</c> row-major. Exposed for zero-copy interop.</summary>
    public ReadOnlySpan<float> Vectors => _store.AsSpan();

    public override bool SupportsReconstruct => true;

    /// <summary>Preallocates storage for an expected vector count, avoiding repeated growth.</summary>
    public void Reserve(int vectors) => _store.Reserve(vectors);

    /// <summary>Releases spare capacity once the index is fully built.</summary>
    public void TrimExcess() => _store.TrimExcess();

    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        _store.Add(x);
        Ntotal = _store.Count;
    }

    public override unsafe void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (nq == 0) return;
        int n = _store.Count;
        // k is deliberately NOT clamped to ntotal: the caller sized its buffers for the k it asked
        // for, so the row stride must stay k. The heap pads the unused slots with -1, as FAISS does.

        if (n == 0)
        {
            distances.Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels.Fill(-1);
            return;
        }

        fixed (float* xq = queries)
        fixed (float* xb = _store.Buffer)
        fixed (float* dis = distances)
        fixed (long* ids = labels)
            BruteForce.Knn(xq, nq, xb, n, D, k, MetricType, dis, ids, null, Threads);
    }

    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius)
    {
        int nq = ValidateBatch(queries, nameof(queries));
        if (nq == 0 || _store.Count == 0)
            return new RangeSearchResult(new long[nq + 1], [], []);

        fixed (float* xq = queries)
        fixed (float* xb = _store.Buffer)
            return BruteForce.RangeSearch(xq, nq, xb, _store.Count, D, radius, MetricType, null, Threads);
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _store.Count)
            throw new ArgumentOutOfRangeException(nameof(key), $"Id {key} is not in [0, {_store.Count}).");
        _store[(int)key].CopyTo(output);
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        int removed = _store.Compact(i => !predicate(i));
        Ntotal = _store.Count;
        return removed;
    }

    public override void Reset()
    {
        _store.Clear();
        Ntotal = 0;
    }

    public override long MemoryUsage => _store.MemoryUsage;

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, {MetricType.ToShortString()}, exact)";

    // --------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.Flat;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(_store.Count);
        var data = _store.AsSpan();
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        _store.Clear();
        _store.Reserve(count);
        var buffer = new float[(long)count * D];
        reader.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()));
        _store.Add(buffer);
        Ntotal = _store.Count;
    }
}

/// <summary>
/// Exact search under squared Euclidean distance — the FAISS baseline, and the type in the
/// canonical three-line example:
/// <code>
/// var index = new IndexFlatL2(dimension: 128);
/// index.Add(vectors);
/// var results = index.Search(query, k: 10);
/// </code>
/// Distances are squared; take <see cref="MathF.Sqrt"/> for true Euclidean distance.
/// </summary>
public sealed class IndexFlatL2 : IndexFlat
{
    public IndexFlatL2(int dimension) : base(dimension, MetricType.L2) { }

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.FlatL2;
}

/// <summary>
/// Exact maximum-inner-product search. L2-normalize both database and queries
/// (<c>Faiss.NormalizeL2</c>) to turn this into exact cosine similarity — the usual setup for text
/// and image embeddings.
/// </summary>
public sealed class IndexFlatIP : IndexFlat
{
    public IndexFlatIP(int dimension) : base(dimension, MetricType.InnerProduct) { }

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.FlatIP;
}
