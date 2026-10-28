using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Makes any index safe to share between threads that both read and write, with a reader-writer
/// lock: searches run concurrently with each other, and every mutation waits for them and runs alone.
/// <para>
/// Most indexes do not need this. <see cref="IndexFlat"/>, the IVF family, the flat code indexes and
/// the HNSW family already let searches run while <em>one</em> writer adds, without any lock (see
/// <c>docs/en/architecture.md</c>, "Concurrency"). Reach for this wrapper when there are several
/// writers, when removals or resets must interleave with searches on an index outside that set
/// (<see cref="IndexShards"/>, <see cref="IndexIDMap2"/>'s reverse table, a GPU index), or when the
/// simplest correct thing is worth more than the last bit of read throughput.
/// </para>
/// <para>
/// The wrapper is transparent to persistence: writing it writes the wrapped index, and reading the
/// file back yields the wrapped type without the lock.
/// </para>
/// </summary>
public sealed class SynchronizedIndex : Index, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>The wrapped index. Touch it directly only when no other thread can.</summary>
    public Index Inner { get; }

    public SynchronizedIndex(Index inner) : base(CheckNotNull(inner).D, inner.MetricType)
    {
        Inner = inner;
        Sync();
    }

    private static Index CheckNotNull(Index inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner;
    }

    private void Sync()
    {
        Ntotal = Inner.Ntotal;
        IsTrained = Inner.IsTrained;
    }

    public override bool SupportsReconstruct => Inner.SupportsReconstruct;

    // --------------------------------------------------------------- Writes

    public override void Train(ReadOnlySpan<float> x)
    {
        _lock.EnterWriteLock();
        try { Inner.Train(x); Sync(); }
        finally { _lock.ExitWriteLock(); }
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        _lock.EnterWriteLock();
        try { Inner.Add(x); Sync(); }
        finally { _lock.ExitWriteLock(); }
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        _lock.EnterWriteLock();
        try { Inner.AddWithIds(x, ids); Sync(); }
        finally { _lock.ExitWriteLock(); }
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        _lock.EnterWriteLock();
        try { long removed = Inner.RemoveIds(ids); Sync(); return removed; }
        finally { _lock.ExitWriteLock(); }
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        _lock.EnterWriteLock();
        try { long removed = Inner.RemoveIds(predicate); Sync(); return removed; }
        finally { _lock.ExitWriteLock(); }
    }

    public override void Reset()
    {
        _lock.EnterWriteLock();
        try { Inner.Reset(); Sync(); }
        finally { _lock.ExitWriteLock(); }
    }

    // ---------------------------------------------------------------- Reads

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        _lock.EnterReadLock();
        try { Inner.Search(queries, nq, k, distances, labels, parameters); }
        finally { _lock.ExitReadLock(); }
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        RangeSearch(queries, radius, parameters: null);

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius, SearchParameters? parameters)
    {
        _lock.EnterReadLock();
        try { return Inner.RangeSearch(queries, radius, parameters: parameters); }
        finally { _lock.ExitReadLock(); }
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        _lock.EnterReadLock();
        try { Inner.Reconstruct(key, output); }
        finally { _lock.ExitReadLock(); }
    }

    public override void ReconstructN(long start, long n, Span<float> output)
    {
        _lock.EnterReadLock();
        try { Inner.ReconstructN(start, n, output); }
        finally { _lock.ExitReadLock(); }
    }

    public override void ComputeDistanceSubset(ReadOnlySpan<float> query, ReadOnlySpan<long> ids, Span<float> distances)
    {
        _lock.EnterReadLock();
        try { Inner.ComputeDistanceSubset(query, ids, distances); }
        finally { _lock.ExitReadLock(); }
    }

    public override long MemoryUsage => Inner.MemoryUsage;

    public override string Describe() => $"SynchronizedIndex({Inner.Describe()})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => Inner.TypeCode;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        _lock.EnterReadLock();
        try { Inner.WriteBody(writer); }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
