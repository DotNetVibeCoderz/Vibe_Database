namespace Faiss.Net;

/// <summary>
/// Storage behind every IVF index: one bucket per coarse centroid, each holding the ids and codes
/// of the vectors assigned to it.
/// <para>
/// Ids and codes live in two parallel arrays per list rather than one array of records. A scan
/// touches codes for every entry but ids only for the handful that survive into the result heap, so
/// separating them keeps the hot stream dense and stops id bytes from evicting code bytes from cache.
/// </para>
/// <para>Lists grow geometrically and independently, which matters because real data is never
/// balanced: a few centroids attract many times the average number of vectors.</para>
/// <para>
/// <b>One writer, many readers.</b> Each list is published as an immutable snapshot (arrays, count,
/// id bounds). A search reads a list once through <see cref="GetList"/> and keeps that view; an
/// append writes past the published count and then publishes a longer snapshot, and a removal
/// rebuilds only the lists it actually changes (copy-on-write). Concurrent writers are not supported.
/// </para>
/// </summary>
public sealed class InvertedLists
{
    private sealed class Bucket(long[] ids, byte[] codes, int count, long minId, long maxId)
    {
        public readonly long[] Ids = ids;
        public readonly byte[] Codes = codes;
        public readonly int Count = count;
        public readonly long MinId = minId;
        public readonly long MaxId = maxId;

        public static readonly Bucket Empty = new([], [], 0, long.MaxValue, long.MinValue);
    }

    /// <summary>A consistent view of one list, valid for as long as the caller holds it.</summary>
    public readonly ref struct ListView
    {
        /// <summary>Ids of the entries.</summary>
        public ReadOnlySpan<long> Ids { get; }

        /// <summary>Codes, <c>Count * CodeSize</c> bytes.</summary>
        public ReadOnlySpan<byte> Codes { get; }

        /// <summary>Entries in the list.</summary>
        public int Count => Ids.Length;

        /// <summary>Smallest id ever stored here (a bound, not exact after removals that kept it).</summary>
        public long MinId { get; }

        /// <summary>Largest id ever stored here.</summary>
        public long MaxId { get; }

        internal ListView(ReadOnlySpan<long> ids, ReadOnlySpan<byte> codes, long minId, long maxId)
        {
            Ids = ids;
            Codes = codes;
            MinId = minId;
            MaxId = maxId;
        }
    }

    private readonly Bucket[] _buckets;

    /// <summary>Number of lists (coarse centroids).</summary>
    public int Nlist { get; }

    /// <summary>Bytes per stored code.</summary>
    public int CodeSize { get; }

    public InvertedLists(int nlist, int codeSize)
    {
        if (nlist <= 0) throw new ArgumentOutOfRangeException(nameof(nlist));
        if (codeSize <= 0) throw new ArgumentOutOfRangeException(nameof(codeSize));
        Nlist = nlist;
        CodeSize = codeSize;
        _buckets = new Bucket[nlist];
        Array.Fill(_buckets, Bucket.Empty);
    }

    private Bucket Read(int list) => Volatile.Read(ref _buckets[list]);

    /// <summary>Entries in one list.</summary>
    public int ListSize(int list) => Read(list).Count;

    /// <summary>One list's ids, codes and id bounds, read together.</summary>
    public ListView GetList(int list)
    {
        var bucket = Read(list);
        return new ListView(bucket.Ids.AsSpan(0, bucket.Count), bucket.Codes.AsSpan(0, bucket.Count * CodeSize),
            bucket.MinId, bucket.MaxId);
    }

    /// <summary>Ids of one list. Use <see cref="GetList"/> when ids and codes must agree.</summary>
    public ReadOnlySpan<long> GetIds(int list)
    {
        var bucket = Read(list);
        return bucket.Ids.AsSpan(0, bucket.Count);
    }

    /// <summary>Codes of one list, <c>ListSize * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> GetCodes(int list)
    {
        var bucket = Read(list);
        return bucket.Codes.AsSpan(0, bucket.Count * CodeSize);
    }

    /// <summary>Appends one entry.</summary>
    public void Add(int list, long id, ReadOnlySpan<byte> code) =>
        AddRange(list, [id], code[..CodeSize]);

    /// <summary>Appends several entries destined for the same list, growing once.</summary>
    public void AddRange(int list, ReadOnlySpan<long> ids, ReadOnlySpan<byte> codes)
    {
        if (ids.Length == 0) return;
        var bucket = _buckets[list];
        int count = bucket.Count;
        int needed = count + ids.Length;

        long[] idArray = bucket.Ids;
        byte[] codeArray = bucket.Codes;
        if (needed > idArray.Length)
        {
            // Fresh arrays rather than Array.Resize: a concurrent search may still hold the old ones.
            int grown = Math.Max(needed, Math.Max(8, idArray.Length + (idArray.Length >> 1)));
            idArray = new long[grown];
            codeArray = new byte[(long)grown * CodeSize];
            bucket.Ids.AsSpan(0, count).CopyTo(idArray);
            bucket.Codes.AsSpan(0, count * CodeSize).CopyTo(codeArray);
        }

        ids.CopyTo(idArray.AsSpan(count));
        codes[..(ids.Length * CodeSize)].CopyTo(codeArray.AsSpan(count * CodeSize));

        long min = bucket.MinId, max = bucket.MaxId;
        foreach (long id in ids)
        {
            if (id < min) min = id;
            if (id > max) max = id;
        }
        // Published last, so a reader that sees the new count also sees the entries behind it.
        Volatile.Write(ref _buckets[list], new Bucket(idArray, codeArray, needed, min, max));
    }

    /// <summary>
    /// Removes entries matching a predicate over ids. Only lists that lose an entry are rebuilt, and
    /// they are rebuilt into new arrays so a concurrent search keeps a valid view of the old ones.
    /// Returns how many entries were removed.
    /// </summary>
    public long RemoveIds(Func<long, bool> predicate)
    {
        long removed = 0;
        for (int list = 0; list < Nlist; list++)
        {
            var bucket = _buckets[list];
            int first = -1;
            for (int i = 0; i < bucket.Count; i++)
                if (predicate(bucket.Ids[i])) { first = i; break; }
            if (first < 0) continue;

            var ids = new long[bucket.Ids.Length];
            var codes = new byte[bucket.Codes.Length];
            bucket.Ids.AsSpan(0, first).CopyTo(ids);
            bucket.Codes.AsSpan(0, first * CodeSize).CopyTo(codes);
            int write = first;
            long min = long.MaxValue, max = long.MinValue;
            for (int i = 0; i < first; i++)
            {
                if (ids[i] < min) min = ids[i];
                if (ids[i] > max) max = ids[i];
            }
            for (int read = first + 1; read < bucket.Count; read++)
            {
                long id = bucket.Ids[read];
                if (predicate(id)) continue;
                ids[write] = id;
                bucket.Codes.AsSpan(read * CodeSize, CodeSize).CopyTo(codes.AsSpan(write * CodeSize, CodeSize));
                if (id < min) min = id;
                if (id > max) max = id;
                write++;
            }
            removed += bucket.Count - write;
            Volatile.Write(ref _buckets[list], new Bucket(ids, codes, write, min, max));
        }
        return removed;
    }

    /// <summary>Empties every list. Buffers are released, since a concurrent search may still read them.</summary>
    public void Reset()
    {
        for (int list = 0; list < Nlist; list++) Volatile.Write(ref _buckets[list], Bucket.Empty);
    }

    /// <summary>Total entries across all lists.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            for (int list = 0; list < Nlist; list++) total += Read(list).Count;
            return total;
        }
    }

    /// <summary>Approximate resident bytes.</summary>
    public long MemoryUsage
    {
        get
        {
            long total = 0;
            for (int list = 0; list < Nlist; list++)
            {
                var bucket = Read(list);
                total += (long)bucket.Ids.Length * sizeof(long) + bucket.Codes.Length;
            }
            return total;
        }
    }

    /// <summary>
    /// Distribution statistics. A large max/mean ratio means the coarse quantizer is unbalanced, so
    /// some queries scan far more candidates than <c>nprobe</c> suggests — the usual explanation for
    /// erratic IVF latency.
    /// </summary>
    public (int Min, int Max, double Mean, int Empty) Statistics()
    {
        int min = int.MaxValue, max = 0, empty = 0;
        long total = 0;
        for (int list = 0; list < Nlist; list++)
        {
            int count = Read(list).Count;
            min = Math.Min(min, count);
            max = Math.Max(max, count);
            total += count;
            if (count == 0) empty++;
        }
        return (min == int.MaxValue ? 0 : min, max, total / (double)Nlist, empty);
    }

    // -------------------------------------------------------- Serialization

    public void Write(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(CodeSize);
        for (int list = 0; list < Nlist; list++)
        {
            var bucket = Read(list);
            writer.Write(bucket.Count);
            for (int i = 0; i < bucket.Count; i++) writer.Write(bucket.Ids[i]);
            writer.Write(bucket.Codes.AsSpan(0, bucket.Count * CodeSize));
        }
    }

    public static InvertedLists Read(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        int codeSize = reader.ReadInt32();
        var lists = new InvertedLists(nlist, codeSize);
        for (int i = 0; i < nlist; i++)
        {
            int count = reader.ReadInt32();
            var ids = new long[count];
            var codes = new byte[(long)count * codeSize];
            long min = long.MaxValue, max = long.MinValue;
            for (int j = 0; j < count; j++)
            {
                long id = reader.ReadInt64();
                ids[j] = id;
                if (id < min) min = id;
                if (id > max) max = id;
            }
            reader.ReadExactly(codes);
            lists._buckets[i] = new Bucket(ids, codes, count, min, max);
        }
        return lists;
    }
}
