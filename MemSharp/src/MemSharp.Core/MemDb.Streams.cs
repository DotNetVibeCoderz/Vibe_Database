using System.Globalization;
using MemSharp.Collections;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>
    /// Appends an entry to a stream, creating it if needed. Returns the id that was assigned.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="fields">Field/value pairs.</param>
    /// <param name="id">
    /// An explicit id, which must be strictly greater than the stream's head. Omit to have one
    /// generated from the clock - within a single millisecond the sequence number increments, so ids
    /// stay strictly ordered however fast the producer runs.
    /// </param>
    /// <param name="maxLength">
    /// If greater than 0, the oldest entries are dropped until at most this many remain. Trimming
    /// from the head is O(1) per dropped entry.
    /// </param>
    public StreamId StreamAdd(
        string key, IEnumerable<KeyValuePair<string, string>> fields, StreamId? id = null, int maxLength = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);

        var flattened = new List<string>();
        foreach (var pair in fields)
        {
            flattened.Add(pair.Key);
            flattened.Add(pair.Value);
        }

        return StreamAdd(key, flattened.ToArray(), id, maxLength);
    }

    /// <summary>
    /// Appends an entry from a pre-flattened field array - <c>[name, value, name, value, ...]</c>.
    /// The allocation-free path, used by the server and the trading demo's hot loop.
    /// </summary>
    public StreamId StreamAdd(string key, string[] fields, StreamId? id = null, int maxLength = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length % 2 != 0) throw new MemSharpCommandException("stream fields must come in name/value pairs");

        var shard = ShardFor(key);
        long now = NowTicks;
        long nowMilliseconds = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        StreamId assigned;

        lock (shard.Gate)
        {
            var stream = GetOrCreate(shard, key, MemType.Stream, now, static () => new StreamStore());
            assigned = stream.Append(id, nowMilliseconds, fields);
            if (maxLength > 0) stream.Trim(maxLength);
        }

        var logged = new string[fields.Length + 2];
        logged[0] = key;
        logged[1] = assigned.ToString();
        Array.Copy(fields, 0, logged, 2, fields.Length);
        RecordWrite("XADD", logged);

        return assigned;
    }

    /// <summary>
    /// Entries with ids in an inclusive range.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="from">Lowest id, inclusive. Defaults to the beginning of the stream.</param>
    /// <param name="to">Highest id, inclusive. Defaults to the end.</param>
    /// <param name="descending">Return newest first.</param>
    /// <param name="limit">Maximum entries, or -1 for all of them.</param>
    public List<StreamEntry> StreamRange(
        string key, StreamId? from = null, StreamId? to = null, bool descending = false, int limit = -1)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream)
                ? stream.Range(from ?? StreamId.Min, to ?? StreamId.Max, descending, limit)
                : new List<StreamEntry>();
        }
    }

    /// <summary>
    /// Entries strictly newer than <paramref name="after"/> - the polling read that a consumer loop
    /// calls with the last id it saw.
    /// </summary>
    public List<StreamEntry> StreamReadAfter(string key, StreamId after, int limit = -1)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        // The range is inclusive, so step one sequence past the caller's cursor rather than
        // returning the entry they have already seen.
        var from = new StreamId(after.Milliseconds, after.Sequence + 1);

        lock (shard.Gate)
        {
            return TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream)
                ? stream.Range(from, StreamId.Max, descending: false, limit)
                : new List<StreamEntry>();
        }
    }

    /// <summary>Number of entries held, or 0 if the stream is absent.</summary>
    public int StreamLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream) ? stream.Count : 0;
        }
    }

    /// <summary>The highest id written, or <c>null</c> if the stream is empty or absent.</summary>
    public StreamId? StreamLastId(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream) || stream.Count == 0)
            {
                return null;
            }
            return stream.LastId;
        }
    }

    /// <summary>Drops the oldest entries until at most <paramref name="maxLength"/> remain.</summary>
    public int StreamTrim(string key, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        int dropped = 0;

        lock (shard.Gate)
        {
            if (TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream))
            {
                dropped = stream.Trim(maxLength);
            }
        }

        if (dropped > 0) RecordWrite("XTRIM", key, "MAXLEN", maxLength.ToString(CultureInfo.InvariantCulture));
        return dropped;
    }
}
