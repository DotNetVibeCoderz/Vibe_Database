using MemSharp.Collections;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Pushes values onto the head of a list. Returns the new length.</summary>
    /// <remarks>
    /// O(1) per value. The list is a ring buffer, so a head push does not shift the elements behind
    /// it - which is what makes a capped feed (push the head, trim the tail) linear rather than
    /// quadratic.
    /// </remarks>
    public int ListPushLeft(string key, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0) return ListLength(key);

        var shard = ShardFor(key);
        long now = NowTicks;
        int length;

        lock (shard.Gate)
        {
            var list = GetOrCreate(shard, key, MemType.List, now, static () => new Deque<string>());
            foreach (var value in values) list.PushFront(value);
            length = list.Count;
        }

        RecordWrite("LPUSH", Prepend(key, values));
        return length;
    }

    /// <summary>Pushes values onto the tail of a list. Returns the new length.</summary>
    public int ListPushRight(string key, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0) return ListLength(key);

        var shard = ShardFor(key);
        long now = NowTicks;
        int length;

        lock (shard.Gate)
        {
            var list = GetOrCreate(shard, key, MemType.List, now, static () => new Deque<string>());
            foreach (var value in values) list.PushBack(value);
            length = list.Count;
        }

        RecordWrite("RPUSH", Prepend(key, values));
        return length;
    }

    /// <summary>Removes and returns the head, or <c>null</c> if the list is empty or absent.</summary>
    public string? ListPopLeft(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        string? value = null;

        lock (shard.Gate)
        {
            if (TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list) && list.TryPopFront(out var popped))
            {
                value = popped;
                RemoveIfEmpty(shard, key, list.Count);
            }
        }

        if (value is not null) RecordWrite("LPOP", key);
        return value;
    }

    /// <summary>Removes and returns the tail, or <c>null</c> if the list is empty or absent.</summary>
    public string? ListPopRight(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        string? value = null;

        lock (shard.Gate)
        {
            if (TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list) && list.TryPopBack(out var popped))
            {
                value = popped;
                RemoveIfEmpty(shard, key, list.Count);
            }
        }

        if (value is not null) RecordWrite("RPOP", key);
        return value;
    }

    /// <summary>
    /// Elements in an inclusive index range. Negative indices count back from the end, so
    /// <c>(0, -1)</c> is the whole list and <c>(-3, -1)</c> the last three.
    /// </summary>
    public List<string> ListRange(string key, int start, int stop)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list)) return new List<string>();

            NormaliseRange(ref start, ref stop, list.Count);
            if (start > stop) return new List<string>();

            return new List<string>(list.Slice(start, stop - start + 1));
        }
    }

    /// <summary>Number of elements in a list, or 0 if it is absent.</summary>
    public int ListLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list) ? list.Count : 0;
        }
    }

    /// <summary>The element at an index, or <c>null</c> if out of range.</summary>
    public string? ListIndex(string key, int index)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list)) return null;
            if (index < 0) index += list.Count;
            return (uint)index < (uint)list.Count ? list[index] : null;
        }
    }

    /// <summary>Overwrites the element at an index. Returns false if the index is out of range.</summary>
    public bool ListSet(string key, int index, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        bool applied = false;

        lock (shard.Gate)
        {
            if (TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list))
            {
                if (index < 0) index += list.Count;
                if ((uint)index < (uint)list.Count)
                {
                    list[index] = value;
                    applied = true;
                }
            }
        }

        if (applied) RecordWrite("LSET", key, index.ToString(), value);
        return applied;
    }

    /// <summary>
    /// Discards everything outside an inclusive index range - the standard way to cap a feed at a
    /// fixed length.
    /// </summary>
    public void ListTrim(string key, int start, int stop)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list)) return;

            NormaliseRange(ref start, ref stop, list.Count);
            if (start > stop) list.Clear();
            else list.KeepRange(start, stop - start + 1);

            RemoveIfEmpty(shard, key, list.Count);
        }

        RecordWrite("LTRIM", key, start.ToString(), stop.ToString());
    }

    /// <summary>
    /// Removes occurrences of a value. <paramref name="count"/> &gt; 0 removes that many from the
    /// head, &lt; 0 from the tail, and 0 removes all of them. Returns how many were removed.
    /// </summary>
    public int ListRemove(string key, string value, int count = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        int removed = 0;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Deque<string>>(shard, key, MemType.List, now, out var list)) return 0;

            int limit = count == 0 ? int.MaxValue : Math.Abs(count);
            var kept = new List<string>(list.Count);

            if (count >= 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (removed < limit && string.Equals(list[i], value, StringComparison.Ordinal)) removed++;
                    else kept.Add(list[i]);
                }
            }
            else
            {
                // Walking backwards removes from the tail; the survivors come out reversed, so flip
                // them once at the end rather than inserting at the head each time.
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (removed < limit && string.Equals(list[i], value, StringComparison.Ordinal)) removed++;
                    else kept.Add(list[i]);
                }
                kept.Reverse();
            }

            if (removed > 0)
            {
                list.Clear();
                foreach (var survivor in kept) list.PushBack(survivor);
                RemoveIfEmpty(shard, key, list.Count);
            }
        }

        if (removed > 0) RecordWrite("LREM", key, count.ToString(), value);
        return removed;
    }

    /// <summary>
    /// Pops the tail of one list and pushes it onto the head of another, atomically. Returns the
    /// moved value, or <c>null</c> if the source was empty.
    /// </summary>
    /// <remarks>
    /// The classic reliable-queue primitive: a worker moves a job onto its own in-flight list in one
    /// step, so a crash between the two halves cannot lose the job.
    /// </remarks>
    public string? ListMove(string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var sourceShard = ShardFor(source);
        var destinationShard = ShardFor(destination);
        long now = NowTicks;
        string? moved;

        if (ReferenceEquals(sourceShard, destinationShard))
        {
            lock (sourceShard.Gate) moved = MoveLocked(sourceShard, destinationShard, source, destination, now);
        }
        else
        {
            var (first, second) = Order(sourceShard, destinationShard);
            lock (first.Gate)
            lock (second.Gate)
            {
                moved = MoveLocked(sourceShard, destinationShard, source, destination, now);
            }
        }

        if (moved is not null) RecordWrite("RPOPLPUSH", source, destination);
        return moved;
    }

    private static string? MoveLocked(Shard sourceShard, Shard destinationShard, string source, string destination, long now)
    {
        if (!TryGetTyped<Deque<string>>(sourceShard, source, MemType.List, now, out var from)) return null;
        if (!from.TryPopBack(out var value)) return null;

        var to = GetOrCreate(destinationShard, destination, MemType.List, now, static () => new Deque<string>());
        to.PushFront(value);
        RemoveIfEmpty(sourceShard, source, from.Count);
        return value;
    }

    /// <summary>Clamps a possibly negative inclusive range onto <c>[0, count - 1]</c>.</summary>
    private static void NormaliseRange(ref int start, ref int stop, int count)
    {
        if (start < 0) start = Math.Max(0, count + start);
        if (stop < 0) stop = count + stop;
        if (stop >= count) stop = count - 1;
        if (start >= count) start = count;   // forces start > stop, i.e. an empty result
    }

    private static string[] Prepend(string key, string[] values)
    {
        var combined = new string[values.Length + 1];
        combined[0] = key;
        Array.Copy(values, 0, combined, 1, values.Length);
        return combined;
    }
}
