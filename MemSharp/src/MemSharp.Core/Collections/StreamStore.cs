namespace MemSharp.Collections;

/// <summary>A stream entry id: milliseconds since the Unix epoch plus a tie-breaking sequence.</summary>
public readonly record struct StreamId(long Milliseconds, long Sequence) : IComparable<StreamId>
{
    /// <summary>The lowest possible id - the start of any stream.</summary>
    public static readonly StreamId Min = new(0, 0);

    /// <summary>The highest possible id - the end of any stream.</summary>
    public static readonly StreamId Max = new(long.MaxValue, long.MaxValue);

    /// <summary>Orders by timestamp, then by sequence.</summary>
    public int CompareTo(StreamId other)
    {
        int byTime = Milliseconds.CompareTo(other.Milliseconds);
        return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
    }

    /// <summary>Renders the id as <c>ms-seq</c>.</summary>
    public override string ToString() => $"{Milliseconds}-{Sequence}";

    /// <summary>Parses <c>ms-seq</c>, or <c>ms</c> with <paramref name="defaultSequence"/> filled in.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, long defaultSequence, out StreamId id)
    {
        id = default;
        int dash = text.IndexOf('-');
        if (dash < 0)
        {
            if (!long.TryParse(text, out long onlyMs)) return false;
            id = new StreamId(onlyMs, defaultSequence);
            return true;
        }

        if (!long.TryParse(text[..dash], out long ms)) return false;
        var tail = text[(dash + 1)..];
        if (tail is "*") { id = new StreamId(ms, defaultSequence); return true; }
        if (!long.TryParse(tail, out long seq)) return false;
        id = new StreamId(ms, seq);
        return true;
    }
}

/// <summary>
/// One entry: an id and its field/value pairs, flattened - <c>Fields[2i]</c> is a name and
/// <c>Fields[2i+1]</c> its value. Flattened rather than a dictionary because entries are small,
/// written far more often than they are searched, and a dictionary per entry would cost more in
/// headers than the data it holds.
/// </summary>
public sealed record StreamEntry(StreamId Id, string[] Fields)
{
    /// <summary>Number of field/value pairs.</summary>
    public int FieldCount => Fields.Length / 2;

    /// <summary>Looks up a field by name, or <c>null</c> if the entry has no such field.</summary>
    public string? this[string field]
    {
        get
        {
            for (int i = 0; i + 1 < Fields.Length; i += 2)
            {
                if (string.Equals(Fields[i], field, StringComparison.Ordinal)) return Fields[i + 1];
            }
            return null;
        }
    }
}

/// <summary>
/// An append-only log of <see cref="StreamEntry"/> in strictly increasing id order, with optional
/// length capping.
/// </summary>
/// <remarks>
/// Backed by <see cref="Deque{T}"/> so trimming from the head is O(1) per dropped entry rather than
/// a shift of the whole log - this is the trade ledger in the trading demo, appended thousands of
/// times a second and capped, which is precisely the pattern that makes a plain list quadratic.
/// Range queries binary-search the ids.
///
/// Not thread-safe. Callers hold the owning shard's lock.
/// </remarks>
internal sealed class StreamStore
{
    private readonly Deque<StreamEntry> _entries = new(16);
    private StreamId _lastId = StreamId.Min;

    public int Count => _entries.Count;
    public StreamId LastId => _lastId;

    /// <summary>
    /// Appends an entry. Pass <c>null</c> for <paramref name="requestedId"/> to auto-generate one
    /// from <paramref name="nowMilliseconds"/>.
    /// </summary>
    public StreamId Append(StreamId? requestedId, long nowMilliseconds, string[] fields)
    {
        StreamId id;
        if (requestedId is { } explicitId)
        {
            if (explicitId.CompareTo(_lastId) <= 0 && _entries.Count > 0)
            {
                throw new MemSharpCommandException(
                    $"the id {explicitId} is not greater than the stream head {_lastId}");
            }
            id = explicitId;
        }
        else
        {
            // Same millisecond as the head means the clock has not moved: bump the sequence so ids
            // stay strictly increasing however fast the producer is.
            id = nowMilliseconds > _lastId.Milliseconds
                ? new StreamId(nowMilliseconds, 0)
                : new StreamId(_lastId.Milliseconds, _lastId.Sequence + 1);
        }

        _entries.PushBack(new StreamEntry(id, fields));
        _lastId = id;
        return id;
    }

    /// <summary>Entries with ids in the inclusive range.</summary>
    public List<StreamEntry> Range(StreamId from, StreamId to, bool descending, int limit)
    {
        var result = new List<StreamEntry>();
        if (_entries.Count == 0) return result;

        if (descending)
        {
            for (int i = UpperBound(to) - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Id.CompareTo(from) < 0) break;
                result.Add(entry);
                if (limit >= 0 && result.Count >= limit) break;
            }
        }
        else
        {
            for (int i = LowerBound(from); i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Id.CompareTo(to) > 0) break;
                result.Add(entry);
                if (limit >= 0 && result.Count >= limit) break;
            }
        }
        return result;
    }

    /// <summary>Drops the oldest entries until at most <paramref name="maxLength"/> remain.</summary>
    public int Trim(int maxLength)
    {
        int dropped = 0;
        while (_entries.Count > maxLength && _entries.TryPopFront(out _)) dropped++;
        return dropped;
    }

    public IEnumerable<StreamEntry> All() => _entries.Enumerate();

    /// <summary>Restores state after a snapshot load, bypassing the ordering check.</summary>
    public void AppendRaw(StreamEntry entry)
    {
        _entries.PushBack(entry);
        if (entry.Id.CompareTo(_lastId) > 0) _lastId = entry.Id;
    }

    private int LowerBound(StreamId id)
    {
        int low = 0, high = _entries.Count;
        while (low < high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            if (_entries[mid].Id.CompareTo(id) < 0) low = mid + 1; else high = mid;
        }
        return low;
    }

    private int UpperBound(StreamId id)
    {
        int low = 0, high = _entries.Count;
        while (low < high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            if (_entries[mid].Id.CompareTo(id) <= 0) low = mid + 1; else high = mid;
        }
        return low;
    }
}
