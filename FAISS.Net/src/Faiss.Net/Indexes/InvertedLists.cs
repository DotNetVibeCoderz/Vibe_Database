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
/// </summary>
public sealed class InvertedLists
{
    private sealed class Bucket
    {
        public long[] Ids = [];
        public byte[] Codes = [];
        public int Count;
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
        for (int i = 0; i < nlist; i++) _buckets[i] = new Bucket();
    }

    /// <summary>Entries in one list.</summary>
    public int ListSize(int list) => _buckets[list].Count;

    /// <summary>Ids of one list.</summary>
    public ReadOnlySpan<long> GetIds(int list) => _buckets[list].Ids.AsSpan(0, _buckets[list].Count);

    /// <summary>Codes of one list, <c>ListSize * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> GetCodes(int list) => _buckets[list].Codes.AsSpan(0, _buckets[list].Count * CodeSize);

    /// <summary>The raw code array of one list; only the first <c>ListSize * CodeSize</c> bytes are live.</summary>
    public byte[] CodeBuffer(int list) => _buckets[list].Codes;

    /// <summary>Appends one entry.</summary>
    public void Add(int list, long id, ReadOnlySpan<byte> code)
    {
        var bucket = _buckets[list];
        EnsureCapacity(bucket, 1);
        bucket.Ids[bucket.Count] = id;
        code[..CodeSize].CopyTo(bucket.Codes.AsSpan(bucket.Count * CodeSize, CodeSize));
        bucket.Count++;
    }

    /// <summary>Appends several entries destined for the same list, growing once.</summary>
    public void AddRange(int list, ReadOnlySpan<long> ids, ReadOnlySpan<byte> codes)
    {
        var bucket = _buckets[list];
        EnsureCapacity(bucket, ids.Length);
        ids.CopyTo(bucket.Ids.AsSpan(bucket.Count));
        codes.CopyTo(bucket.Codes.AsSpan(bucket.Count * CodeSize));
        bucket.Count += ids.Length;
    }

    private void EnsureCapacity(Bucket bucket, int additional)
    {
        int needed = bucket.Count + additional;
        if (needed > bucket.Ids.Length)
        {
            int grown = Math.Max(needed, Math.Max(8, bucket.Ids.Length + (bucket.Ids.Length >> 1)));
            Array.Resize(ref bucket.Ids, grown);
            Array.Resize(ref bucket.Codes, grown * CodeSize);
        }
    }

    /// <summary>
    /// Removes entries matching a predicate over ids, compacting each list in place.
    /// Returns how many entries were removed.
    /// </summary>
    public long RemoveIds(Func<long, bool> predicate)
    {
        long removed = 0;
        foreach (var bucket in _buckets)
        {
            int write = 0;
            for (int read = 0; read < bucket.Count; read++)
            {
                if (predicate(bucket.Ids[read])) continue;
                if (write != read)
                {
                    bucket.Ids[write] = bucket.Ids[read];
                    bucket.Codes.AsSpan(read * CodeSize, CodeSize)
                                .CopyTo(bucket.Codes.AsSpan(write * CodeSize, CodeSize));
                }
                write++;
            }
            removed += bucket.Count - write;
            bucket.Count = write;
        }
        return removed;
    }

    /// <summary>Empties every list, keeping the allocated buffers.</summary>
    public void Reset()
    {
        foreach (var bucket in _buckets) bucket.Count = 0;
    }

    /// <summary>Total entries across all lists.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (var bucket in _buckets) total += bucket.Count;
            return total;
        }
    }

    /// <summary>Approximate resident bytes.</summary>
    public long MemoryUsage
    {
        get
        {
            long total = 0;
            foreach (var bucket in _buckets)
                total += (long)bucket.Ids.Length * sizeof(long) + bucket.Codes.Length;
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
        foreach (var bucket in _buckets)
        {
            min = Math.Min(min, bucket.Count);
            max = Math.Max(max, bucket.Count);
            total += bucket.Count;
            if (bucket.Count == 0) empty++;
        }
        return (min == int.MaxValue ? 0 : min, max, total / (double)Nlist, empty);
    }

    // -------------------------------------------------------- Serialization

    public void Write(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(CodeSize);
        foreach (var bucket in _buckets)
        {
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
            var bucket = lists._buckets[i];
            bucket.Ids = new long[count];
            bucket.Codes = new byte[(long)count * codeSize];
            for (int j = 0; j < count; j++) bucket.Ids[j] = reader.ReadInt64();
            reader.ReadExactly(bucket.Codes);
            bucket.Count = count;
        }
        return lists;
    }
}
