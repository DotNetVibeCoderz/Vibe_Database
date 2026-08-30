using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Wraps any index so it can carry arbitrary 64-bit ids — database primary keys, hashes, timestamps
/// — instead of the sequential positions the underlying index assigns.
/// <para>
/// The wrapper keeps a position-to-id table and rewrites labels on the way out. That indirection is
/// what makes deletion safe for callers: removing an entry renumbers positions inside the wrapped
/// index, but the ids the application holds never change.
/// </para>
/// </summary>
public class IndexIDMap : Index
{
    /// <summary>The wrapped index. Its own ids are positions in <see cref="IdMap"/>.</summary>
    public Index Base { get; private set; }

    /// <summary>Position-to-id table; entry <c>i</c> is the caller's id for the wrapped index's id <c>i</c>.</summary>
    public List<long> IdMap { get; private set; } = [];

    public IndexIDMap(Index baseIndex) : base(baseIndex.D, baseIndex.MetricType)
    {
        Base = baseIndex;
        IsTrained = baseIndex.IsTrained;
    }

    public override bool SupportsReconstruct => Base.SupportsReconstruct;

    public override void Train(ReadOnlySpan<float> x)
    {
        Base.Train(x);
        IsTrained = Base.IsTrained;
    }

    /// <summary>
    /// Adds with sequential ids continuing from the current count. FAISS rejects this call on an
    /// <c>IndexIDMap</c>; here it is allowed because the natural default is unambiguous, and the
    /// explicit form remains available.
    /// </summary>
    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        var ids = new long[n];
        long next = IdMap.Count == 0 ? 0 : IdMap.Max() + 1;
        for (int i = 0; i < n; i++) ids[i] = next + i;
        AddWithIds(x, ids);
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        int n = ValidateBatch(x, nameof(x));
        if (ids.Length != n) throw new ArgumentException($"Expected {n} ids, got {ids.Length}.", nameof(ids));

        Base.Add(x);
        foreach (long id in ids) IdMap.Add(id);
        Ntotal = Base.Ntotal;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        Base.Search(queries, nq, k, distances, labels);
        Translate(labels);
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius)
    {
        var result = Base.RangeSearch(queries, radius);
        Translate(result.Labels);
        return result;
    }

    private void Translate(Span<long> labels)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            long position = labels[i];
            labels[i] = position >= 0 && position < IdMap.Count ? IdMap[(int)position] : -1;
        }
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        // The wrapped index sees positions; translate the id predicate into a position predicate,
        // then compact the id table the same way the wrapped index compacts its storage.
        long removed = Base.RemoveIds(position =>
            position >= 0 && position < IdMap.Count && predicate(IdMap[(int)position]));

        var kept = new List<long>(IdMap.Count);
        foreach (long id in IdMap)
            if (!predicate(id)) kept.Add(id);
        IdMap = kept;

        Ntotal = Base.Ntotal;
        return removed;
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        int position = IdMap.IndexOf(key);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(key), $"Id {key} is not in the index.");
        Base.Reconstruct(position, output);
    }

    public override void Reset()
    {
        Base.Reset();
        IdMap.Clear();
        Ntotal = 0;
    }

    public override long MemoryUsage => Base.MemoryUsage + (long)IdMap.Count * sizeof(long);

    public override string Describe() => $"IndexIDMap({Base.Describe()})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IDMap;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        IndexIO.WriteTo(writer, Base);
        writer.Write(IdMap.Count);
        foreach (long id in IdMap) writer.Write(id);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        Base = IndexIO.ReadFrom(reader);
        int count = reader.ReadInt32();
        IdMap = new List<long>(count);
        for (int i = 0; i < count; i++) IdMap.Add(reader.ReadInt64());
        Ntotal = Base.Ntotal;
        IsTrained = Base.IsTrained;
    }
}

/// <summary>
/// <see cref="IndexIDMap"/> plus a reverse id-to-position table, so <see cref="Reconstruct(long, Span{float})"/>
/// is a hash lookup instead of a linear scan. Worth the extra table whenever vectors are fetched by
/// id, which is the normal pattern when the index sits behind an application database.
/// </summary>
public sealed class IndexIDMap2 : IndexIDMap
{
    private Dictionary<long, int> _reverse = [];

    public IndexIDMap2(Index baseIndex) : base(baseIndex) { }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        int start = IdMap.Count;
        base.AddWithIds(x, ids);
        for (int i = 0; i < ids.Length; i++) _reverse[ids[i]] = start + i;
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (!_reverse.TryGetValue(key, out int position))
            throw new ArgumentOutOfRangeException(nameof(key), $"Id {key} is not in the index.");
        Base.Reconstruct(position, output);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        long removed = base.RemoveIds(predicate);
        RebuildReverse();
        return removed;
    }

    public override void Reset()
    {
        base.Reset();
        _reverse.Clear();
    }

    private void RebuildReverse()
    {
        _reverse = new Dictionary<long, int>(IdMap.Count);
        for (int i = 0; i < IdMap.Count; i++) _reverse[IdMap[i]] = i;
    }

    public override long MemoryUsage => base.MemoryUsage + (long)_reverse.Count * 16;

    public override string Describe() => $"IndexIDMap2({Base.Describe()})";

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IDMap2;

    protected internal override void ReadBody(BinaryReader reader)
    {
        base.ReadBody(reader);
        RebuildReverse();
    }
}
