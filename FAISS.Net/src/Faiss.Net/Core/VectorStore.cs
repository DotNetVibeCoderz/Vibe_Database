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
/// </summary>
public sealed class VectorStore
{
    private float[] _data;

    /// <summary>Vector dimension.</summary>
    public int Dimension { get; }

    /// <summary>Number of vectors stored.</summary>
    public int Count { get; private set; }

    /// <summary>Vectors that fit before the next reallocation.</summary>
    public int Capacity => Dimension == 0 ? 0 : _data.Length / Dimension;

    public VectorStore(int dimension, int initialCapacity = 0)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        Dimension = dimension;
        _data = initialCapacity > 0 ? new float[(long)initialCapacity * dimension] : [];
    }

    /// <summary>The raw backing array. Only the first <c>Count * Dimension</c> entries are live.</summary>
    public float[] Buffer => _data;

    /// <summary>All live vectors as one flat span.</summary>
    public Span<float> AsSpan() => _data.AsSpan(0, Count * Dimension);

    /// <summary>One vector by index.</summary>
    public Span<float> this[int index] => _data.AsSpan(index * Dimension, Dimension);

    /// <summary>Grows the backing array so <paramref name="vectors"/> more vectors fit without reallocating.</summary>
    public void Reserve(int vectors)
    {
        long needed = (long)(Count + vectors) * Dimension;
        if (needed <= _data.Length) return;
        long grown = Math.Max(needed, _data.Length + (_data.Length >> 1));
        if (grown > Array.MaxLength) grown = needed;
        if (grown > Array.MaxLength)
            throw new InvalidOperationException(
                $"Index would exceed the maximum .NET array size ({Array.MaxLength} floats). " +
                "Use a compressed index (IndexIVFPQ) or shard across several indexes.");
        Array.Resize(ref _data, (int)grown);
    }

    /// <summary>Appends <c>span.Length / Dimension</c> vectors.</summary>
    public void Add(ReadOnlySpan<float> vectors)
    {
        if (vectors.Length % Dimension != 0)
            throw new ArgumentException(
                $"Input length {vectors.Length} is not a multiple of dimension {Dimension}.");
        int n = vectors.Length / Dimension;
        Reserve(n);
        vectors.CopyTo(_data.AsSpan(Count * Dimension));
        Count += n;
    }

    /// <summary>
    /// Removes vectors by index. Order is not preserved: each removed slot is filled from the end,
    /// which keeps removal O(number removed) instead of O(n) per deletion. Returns the number removed.
    /// </summary>
    /// <param name="sortedIndices">Indices to drop, ascending and unique.</param>
    /// <param name="movedFrom">
    /// Receives, per removal, the index the surviving vector was moved from, so callers can keep
    /// their own id tables in sync.
    /// </param>
    public int RemoveAt(ReadOnlySpan<int> sortedIndices, IList<(int To, int From)>? movedFrom = null)
    {
        int removed = 0;
        for (int i = sortedIndices.Length - 1; i >= 0; i--)
        {
            int index = sortedIndices[i];
            if (index < 0 || index >= Count) continue;
            int last = Count - 1;
            if (index != last)
            {
                _data.AsSpan(last * Dimension, Dimension).CopyTo(_data.AsSpan(index * Dimension, Dimension));
                movedFrom?.Add((index, last));
            }
            Count--;
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Compacts the store in place, keeping only vectors for which <paramref name="keep"/> is true
    /// and preserving relative order. Order-preserving removal is what <c>IndexFlat</c> needs,
    /// because its ids are positions: renumbering must stay monotonic, as it does in FAISS.
    /// Returns the number of vectors removed.
    /// </summary>
    public int Compact(Func<int, bool> keep)
    {
        int write = 0;
        for (int read = 0; read < Count; read++)
        {
            if (!keep(read)) continue;
            if (write != read)
                _data.AsSpan(read * Dimension, Dimension).CopyTo(_data.AsSpan(write * Dimension, Dimension));
            write++;
        }
        int removed = Count - write;
        Count = write;
        return removed;
    }

    /// <summary>Drops all vectors, keeping the allocated capacity for reuse.</summary>
    public void Clear() => Count = 0;

    /// <summary>Releases unused capacity. Worth calling once an index is fully built.</summary>
    public void TrimExcess()
    {
        int needed = Count * Dimension;
        if (_data.Length > needed) Array.Resize(ref _data, needed);
    }

    /// <summary>Approximate resident bytes, used by the memory reports in the samples and gallery.</summary>
    public long MemoryUsage => (long)_data.Length * sizeof(float);
}
