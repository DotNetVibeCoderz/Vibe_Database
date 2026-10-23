using MemSharp.Collections;

namespace MemSharp;

/// <summary>One page of a cursor-driven keyspace scan.</summary>
/// <param name="Cursor">Pass back to continue; 0 means the scan is complete.</param>
/// <param name="Keys">Keys in this page. May be empty even when <paramref name="Cursor"/> is not 0.</param>
public readonly record struct ScanResult(long Cursor, List<string> Keys);

public sealed partial class MemDb
{
    /// <summary>
    /// One page of a resumable keyspace scan - the engine behind <c>SCAN</c>.
    /// </summary>
    /// <param name="cursor">0 to start, then whatever the previous page returned.</param>
    /// <param name="pattern">Glob the keys must match.</param>
    /// <param name="count">Roughly how many keys to return per page.</param>
    /// <remarks>
    /// <para>
    /// The guarantee is the one Redis makes: a key present for the whole iteration is returned at
    /// least once, however the keyspace changes in between. A key added or removed mid-scan may or
    /// may not appear, and a key can occasionally appear twice.
    /// </para>
    /// <para>
    /// 1.0's cursor was an offset into the concatenated shards, which broke that guarantee: removing
    /// a key from an earlier position shifted every later key down one, and the key at the page
    /// boundary was skipped. The cursor now encodes a shard index in its high 32 bits and a hash
    /// threshold in its low 32. Within a shard, keys are visited in order of their hash, and the
    /// threshold says where the last page stopped. A key's hash never changes and a key never moves
    /// shard, so no insertion or deletion anywhere can move a key behind the cursor. Every key
    /// sharing the last hash of a page is returned in that page, so a collision cannot split
    /// across the boundary either.
    /// </para>
    /// <para>
    /// The cost is a pass over the current shard per page - O(keys per shard) rather than O(count).
    /// That is cheaper than 1.0, whose offset cursor re-walked every earlier shard on every page.
    /// The hash is per-process, so a cursor does not survive a restart.
    /// </para>
    /// </remarks>
    public ScanResult ScanPage(long cursor, string pattern = "*", int count = 10)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (cursor < 0) throw new MemSharpCommandException("invalid cursor");
        if (count < 1) count = 1;

        int shardIndex = (int)Math.Min(cursor >> 32, int.MaxValue);
        uint threshold = (uint)cursor;
        var page = new List<string>();
        bool everything = pattern == "*";

        while (shardIndex < _shards.Length)
        {
            var shard = _shards[shardIndex];
            var candidates = new List<(uint Hash, string Key)>();
            long now = NowTicks;

            lock (shard.Gate)
            {
                foreach (var pair in shard.Map)
                {
                    if (pair.Value.IsExpired(now)) continue;
                    uint hash = (uint)pair.Key.GetHashCode();
                    if (hash >= threshold) candidates.Add((hash, pair.Key));
                }
            }

            // Matching and sorting happen outside the lock; the candidates are a private copy.
            if (!everything) candidates.RemoveAll(c => !GlobMatcher.IsMatch(pattern, c.Key));
            candidates.Sort(static (a, b) => a.Hash != b.Hash ? a.Hash.CompareTo(b.Hash) : string.CompareOrdinal(a.Key, b.Key));

            int room = count - page.Count;
            if (candidates.Count <= room)
            {
                foreach (var candidate in candidates) page.Add(candidate.Key);
                shardIndex++;
                threshold = 0;
                if (page.Count >= count) break;
                continue;
            }

            // Take what fits, then everything else that shares the last hash taken - see remarks.
            int taken = room;
            uint last = candidates[taken - 1].Hash;
            while (taken < candidates.Count && candidates[taken].Hash == last) taken++;
            for (int i = 0; i < taken; i++) page.Add(candidates[i].Key);

            if (last == uint.MaxValue)
            {
                shardIndex++;
                threshold = 0;
            }
            else
            {
                threshold = last + 1;
            }
            break;
        }

        long next = shardIndex >= _shards.Length ? 0 : ((long)shardIndex << 32) | threshold;
        return new ScanResult(next, page);
    }

    /// <summary>
    /// Estimated bytes of managed memory a key holds - its value, its key string and its slot in
    /// the shard - or <c>null</c> if it is absent.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="samples">
    /// For collections, how many elements to measure and extrapolate from; 0 measures every one.
    /// Defaults to 5, like Redis's <c>MEMORY USAGE</c>, so the call stays cheap on a huge key.
    /// </param>
    /// <remarks>
    /// An estimate built from the 64-bit CLR's object layout - headers, array overheads, string
    /// lengths - not a heap walk. It is meant for "which keys are big", where it is reliable, and
    /// not for exact accounting: allocator rounding, spare collection capacity and strings shared
    /// between keys are not modelled.
    /// </remarks>
    public long? MemoryUsage(string key, int samples = 5)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (samples < 0) throw new MemSharpCommandException("SAMPLES can't be negative");

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry)) return null;
            return MemoryEstimate.SlotBytes + MemoryEstimate.Of(key) + MemoryEstimate.Of(entry.Type, entry.Value, samples);
        }
    }

    /// <summary>Keys carrying a TTL, summed across shards. Includes expired keys not yet evicted.</summary>
    internal long VolatileKeyCount
    {
        get
        {
            long total = 0;
            foreach (var shard in _shards) total += Volatile.Read(ref shard.VolatileCount);
            return total;
        }
    }

    internal MemDbOptions Options => _options;
}

/// <summary>Object-layout arithmetic behind <see cref="MemDb.MemoryUsage"/>.</summary>
internal static class MemoryEstimate
{
    // 64-bit CLR: an object is an 8-byte header plus an 8-byte method-table pointer, rounded to 8.
    private const long ObjectHeader = 16;
    private const long Reference = 8;

    /// <summary>A shard dictionary slot: hash, next, key reference and the by-value <see cref="StoreEntry"/>, plus its bucket.</summary>
    public const long SlotBytes = 4 + 4 + Reference + 24 + 4;

    // Per-entry cost of the BCL collections: Dictionary<string,string> stores hash, next, key and
    // value (24 bytes) plus a 4-byte bucket; HashSet<string> drops the value.
    private const long DictionaryEntry = 28;
    private const long HashSetEntry = 20;
    private const long CollectionObject = 80;

    public static long Of(string text) => Align(ObjectHeader + 4 + (2L * text.Length) + 2);

    private static long Align(long bytes) => (bytes + 7) & ~7L;

    private static long Array(long elements, long elementSize) => Align(ObjectHeader + 8 + (elements * elementSize));

    public static long Of(MemType type, object value, int samples) => type switch
    {
        MemType.String => Of((string)value),
        MemType.List => ListBytes((Deque<string>)value, samples),
        MemType.Hash => HashBytes((Dictionary<string, string>)value, samples),
        MemType.Set => SetBytes((HashSet<string>)value, samples),
        MemType.SortedSet => ((SortedSetStore)value).EstimateBytes(samples),
        MemType.TimeSeries => ((TimeSeriesStore)value).EstimateBytes(),
        MemType.Stream => ((StreamStore)value).EstimateBytes(samples),
        _ => 0,
    };

    /// <summary>
    /// Average cost per element of <paramref name="items"/>, measured on at most
    /// <paramref name="samples"/> of them (all when 0), times <paramref name="count"/>.
    /// </summary>
    public static long Extrapolate<T>(IEnumerable<T> items, int count, int samples, Func<T, long> measure)
    {
        if (count == 0) return 0;
        long measured = 0;
        int seen = 0;
        foreach (var item in items)
        {
            measured += measure(item);
            if (++seen == samples) break;
        }
        return seen == 0 ? 0 : measured * count / seen;
    }

    private static long ListBytes(Deque<string> list, int samples) =>
        ObjectHeader + 16 + Array(list.Count, Reference) + Extrapolate(list.Enumerate(), list.Count, samples, Of);

    private static long HashBytes(Dictionary<string, string> hash, int samples) =>
        CollectionObject + (hash.Count * DictionaryEntry) +
        Extrapolate(hash, hash.Count, samples, static pair => Of(pair.Key) + Of(pair.Value));

    private static long SetBytes(HashSet<string> set, int samples) =>
        CollectionObject + (set.Count * HashSetEntry) + Extrapolate(set, set.Count, samples, Of);

    public static long StringArray(string[] items)
    {
        long total = Array(items.Length, Reference);
        foreach (var item in items) total += Of(item);
        return total;
    }

    /// <summary>A dictionary-entry-plus-bucket's worth of bytes, for the stores that keep a member map.</summary>
    public const long MapEntry = DictionaryEntry;

    public const long Header = ObjectHeader;
}
