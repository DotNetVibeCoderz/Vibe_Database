namespace Faiss.Net.Core;

/// <summary>
/// Growable row-major store of fixed-dimension vectors, backed by one contiguous
/// <see cref="float"/> array.
/// <para>
/// One array rather than an array of arrays is the whole point: a scan walks memory sequentially,
/// the hardware prefetcher keeps up, and there is a single object for the GC to track no matter how
/// many vectors are stored. Growth is 1.5x rather than 2x, which keeps the transient peak during a
/// resize near 2.5x the live set instead of 3x — the difference decides whether a large index fits
/// in RAM.
/// </para>
/// <para>
/// <b>One writer, many readers.</b> The backing array and the live count are published together as
/// one immutable snapshot, so a search that takes a <see cref="Snapshot"/> keeps a consistent view
/// while a single writer appends. Appends that fit in capacity write past the published count and
/// only then publish the longer snapshot; a reader holding the old one never looks there. Anything
/// that would rewrite live slots — compaction, clearing, trimming — builds a new array instead
/// (copy-on-write), because a reader may still be scanning the old one. Concurrent <em>writers</em>
/// are not supported.
/// </para>
/// </summary>
public sealed class VectorStore
{
    private sealed class State(float[] data, int count)
    {
        public readonly float[] Data = data;
        public readonly int Count = count;
    }

    private State _state;

    /// <summary>Vector dimension.</summary>
    public int Dimension { get; }

    /// <summary>Number of vectors stored.</summary>
    public int Count => Volatile.Read(ref _state).Count;

    /// <summary>Vectors that fit before the next reallocation.</summary>
    public int Capacity => Volatile.Read(ref _state).Data.Length / Dimension;

    public VectorStore(int dimension, int initialCapacity = 0)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        Dimension = dimension;
        _state = new State(initialCapacity > 0 ? new float[(long)initialCapacity * dimension] : [], 0);
    }

    /// <summary>
    /// The backing array and live count, read together. Searches take one snapshot up front and use
    /// only it, which is what makes them safe against a concurrent append.
    /// </summary>
    public (float[] Data, int Count) Snapshot()
    {
        var state = Volatile.Read(ref _state);
        return (state.Data, state.Count);
    }

    /// <summary>
    /// The raw backing array. Only the first <c>Count * Dimension</c> entries are live. Read
    /// <see cref="Count"/> <em>before</em> this property when the two are used together, or use
    /// <see cref="Snapshot"/>: the array read second is then at least as long as the count implies.
    /// </summary>
    public float[] Buffer => Volatile.Read(ref _state).Data;

    /// <summary>All live vectors as one flat span.</summary>
    public Span<float> AsSpan()
    {
        var state = Volatile.Read(ref _state);
        return state.Data.AsSpan(0, state.Count * Dimension);
    }

    /// <summary>One vector by index.</summary>
    public Span<float> this[int index]
    {
        get
        {
            var state = Volatile.Read(ref _state);
            if ((uint)index >= (uint)state.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return state.Data.AsSpan(index * Dimension, Dimension);
        }
    }

    /// <summary>Grows the backing array so <paramref name="vectors"/> more vectors fit without reallocating.</summary>
    public void Reserve(int vectors)
    {
        var state = _state;
        float[] data = Grow(state, vectors);
        if (!ReferenceEquals(data, state.Data)) Volatile.Write(ref _state, new State(data, state.Count));
    }

    /// <summary>Returns the current array if it has room, otherwise a larger copy of its live part.</summary>
    private float[] Grow(State state, int vectors)
    {
        long needed = (long)(state.Count + vectors) * Dimension;
        if (needed <= state.Data.Length) return state.Data;
        long grown = Math.Max(needed, state.Data.Length + (state.Data.Length >> 1));
        if (grown > Array.MaxLength) grown = needed;
        if (grown > Array.MaxLength)
            throw new InvalidOperationException(
                $"Index would exceed the maximum .NET array size ({Array.MaxLength} floats). " +
                "Use a compressed index (IndexIVFPQ) or shard across several indexes.");

        // A fresh array, not Array.Resize in place: readers may still hold the old one.
        var data = new float[grown];
        state.Data.AsSpan(0, state.Count * Dimension).CopyTo(data);
        return data;
    }

    /// <summary>Appends <c>span.Length / Dimension</c> vectors.</summary>
    public void Add(ReadOnlySpan<float> vectors)
    {
        if (vectors.Length % Dimension != 0)
            throw new ArgumentException(
                $"Input length {vectors.Length} is not a multiple of dimension {Dimension}.");
        int n = vectors.Length / Dimension;
        if (n == 0) return;

        var state = _state;
        float[] data = Grow(state, n);
        vectors.CopyTo(data.AsSpan(state.Count * Dimension));
        // Publish after the copy: a reader that sees the new count also sees the vectors.
        Volatile.Write(ref _state, new State(data, state.Count + n));
    }

    /// <summary>
    /// Keeps only vectors for which <paramref name="keep"/> is true, preserving relative order.
    /// Order-preserving removal is what <c>IndexFlat</c> needs, because its ids are positions:
    /// renumbering must stay monotonic, as it does in FAISS. Builds a new array so concurrent readers
    /// keep scanning the old one undisturbed. Returns the number of vectors removed.
    /// </summary>
    public int Compact(Func<int, bool> keep)
    {
        var state = _state;
        var data = new float[state.Data.Length];
        int write = 0;
        for (int read = 0; read < state.Count; read++)
        {
            if (!keep(read)) continue;
            state.Data.AsSpan(read * Dimension, Dimension).CopyTo(data.AsSpan(write * Dimension, Dimension));
            write++;
        }
        int removed = state.Count - write;
        if (removed > 0) Volatile.Write(ref _state, new State(data, write));
        return removed;
    }

    /// <summary>
    /// Drops all vectors. The capacity is released rather than reused, since a concurrent reader may
    /// still be scanning the old array.
    /// </summary>
    public void Clear() => Volatile.Write(ref _state, new State([], 0));

    /// <summary>Releases unused capacity. Worth calling once an index is fully built.</summary>
    public void TrimExcess()
    {
        var state = _state;
        int needed = state.Count * Dimension;
        if (state.Data.Length == needed) return;
        Volatile.Write(ref _state, new State(state.Data.AsSpan(0, needed).ToArray(), state.Count));
    }

    /// <summary>Approximate resident bytes, used by the memory reports in the samples and gallery.</summary>
    public long MemoryUsage => (long)Volatile.Read(ref _state).Data.Length * sizeof(float);
}
