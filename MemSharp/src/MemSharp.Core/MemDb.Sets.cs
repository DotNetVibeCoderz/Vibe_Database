namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Adds members to a set. Returns how many were new.</summary>
    public int SetAdd(string key, params string[] members)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Length == 0) return 0;

        var shard = ShardFor(key);
        long now = NowTicks;
        int added = 0;

        lock (shard.Gate)
        {
            var set = GetOrCreate(shard, key, MemType.Set, now, static () => new HashSet<string>(StringComparer.Ordinal));
            foreach (var member in members)
            {
                if (set.Add(member)) added++;
            }
            RecordWrite(shard, "SADD", Prepend(key, members));
        }
        return added;
    }

    /// <summary>Removes members from a set. Returns how many were present.</summary>
    public int SetRemove(string key, params string[] members)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(members);

        var shard = ShardFor(key);
        long now = NowTicks;
        int removed = 0;

        lock (shard.Gate)
        {
            if (!TryGetTyped<HashSet<string>>(shard, key, MemType.Set, now, out var set)) return 0;
            foreach (var member in members)
            {
                if (set.Remove(member)) removed++;
            }
            RemoveIfEmpty(shard, key, set.Count);
            if (removed > 0) RecordWrite(shard, "SREM", Prepend(key, members));
        }
        return removed;
    }

    /// <summary>True if a set contains a member.</summary>
    public bool SetContains(string key, string member)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<HashSet<string>>(shard, key, MemType.Set, now, out var set) && set.Contains(member);
        }
    }

    /// <summary>
    /// A copy of every member. Copied rather than handed out live - the original engine returned the
    /// backing set itself, which let callers mutate the database with no lock held.
    /// </summary>
    public HashSet<string> SetMembers(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<HashSet<string>>(shard, key, MemType.Set, now, out var set)
                ? new HashSet<string>(set, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Number of members in a set, or 0 if it is absent.</summary>
    public int SetLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<HashSet<string>>(shard, key, MemType.Set, now, out var set) ? set.Count : 0;
        }
    }

    /// <summary>Removes and returns an arbitrary member, or <c>null</c> if the set is empty.</summary>
    public string? SetPop(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        string? popped = null;

        lock (shard.Gate)
        {
            if (TryGetTyped<HashSet<string>>(shard, key, MemType.Set, now, out var set) && set.Count > 0)
            {
                foreach (var member in set) { popped = member; break; }
                if (popped is not null)
                {
                    set.Remove(popped);
                    RemoveIfEmpty(shard, key, set.Count);
                }
            }
            if (popped is not null) RecordWrite(shard, "SREM", key, popped);
        }
        return popped;
    }

    /// <summary>Members present in every named set.</summary>
    public HashSet<string> SetIntersect(params string[] keys) => Combine(SetOperation.Intersect, keys);

    /// <summary>Members present in any named set.</summary>
    public HashSet<string> SetUnion(params string[] keys) => Combine(SetOperation.Union, keys);

    /// <summary>Members of the first set that appear in none of the others.</summary>
    public HashSet<string> SetDifference(params string[] keys) => Combine(SetOperation.Difference, keys);

    /// <summary>
    /// Stores the intersection of <paramref name="keys"/> at <paramref name="destination"/>, replacing
    /// it. Returns the size of the result; an empty result deletes the destination.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SetIntersect"/>, this is a point-in-time operation: every shard involved -
    /// the sources and the destination - is locked, in shard order, for the whole computation. The
    /// read-only algebra can afford a result stitched together from reads taken microseconds apart,
    /// because it is gone as soon as the caller has looked at it. A stored result is durable, and
    /// a looseness that was transient would become permanent - persisted, replicated into the
    /// append-only log, and read back by everyone. Holding several locks costs throughput on those
    /// shards for the length of the operation; that is the price of the result being true.
    /// </remarks>
    public int SetIntersectStore(string destination, params string[] keys) => CombineStore(SetOperation.Intersect, destination, keys);

    /// <summary>Stores the union of <paramref name="keys"/> at <paramref name="destination"/>. See <see cref="SetIntersectStore"/>.</summary>
    public int SetUnionStore(string destination, params string[] keys) => CombineStore(SetOperation.Union, destination, keys);

    /// <summary>Stores the first set minus the others at <paramref name="destination"/>. See <see cref="SetIntersectStore"/>.</summary>
    public int SetDifferenceStore(string destination, params string[] keys) => CombineStore(SetOperation.Difference, destination, keys);

    private enum SetOperation { Union, Intersect, Difference }

    private int CombineStore(SetOperation operation, string destination, string[] keys)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0) throw new MemSharpCommandException("at least one source key is required");

        var shards = ShardsInOrder(destination, keys);
        long now = NowTicks;

        EnterAll(shards);
        try
        {
            // Copy the first operand: it may be the destination itself, which is about to be replaced.
            var result = ReadSetLocked(keys[0], now) is { } first
                ? new HashSet<string>(first, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            for (int i = 1; i < keys.Length; i++)
            {
                if (result.Count == 0 && operation != SetOperation.Union) break;
                var other = ReadSetLocked(keys[i], now);
                switch (operation)
                {
                    case SetOperation.Union: if (other is not null) result.UnionWith(other); break;
                    case SetOperation.Intersect: if (other is null) result.Clear(); else result.IntersectWith(other); break;
                    case SetOperation.Difference: if (other is not null) result.ExceptWith(other); break;
                }
            }

            var destinationShard = ShardFor(destination);
            Overwrite(destinationShard, destination, MemType.Set, result.Count > 0 ? result : null);

            // Logged as the result, not as the command. Replaying "SINTERSTORE" would recompute from
            // whatever the sources hold at that point in the log, which is the same answer only if
            // nothing about the sources' history ever differs from the live run. Only the
            // destination is mutated, so only its shard decides whether a snapshot already has it.
            RecordWrite(destinationShard, "DEL", destination);
            if (result.Count > 0) RecordWrite(destinationShard, "SADD", Prepend(destination, [.. result]));
            return result.Count;
        }
        finally
        {
            ExitAll(shards);
        }
    }

    /// <summary>The live set at a key, or null if absent. Caller holds the key's gate.</summary>
    private HashSet<string>? ReadSetLocked(string key, long now) =>
        TryGetTyped<HashSet<string>>(ShardFor(key), key, MemType.Set, now, out var set) ? set : null;

    /// <summary>
    /// Applies a set operation across keys that may live on different shards.
    /// </summary>
    /// <remarks>
    /// Each set is snapshotted under its own lock and the algebra runs afterwards, rather than
    /// holding every shard lock for the duration. That means the result is not a point-in-time view
    /// across all the inputs - a concurrent write to a later key can land after an earlier key was
    /// read. The alternative is holding N locks while doing O(total) work, which would stall every
    /// writer that happens to hash to those shards; for the read-mostly analytics these operations
    /// serve, the snapshot is the better trade. Single-key operations remain fully atomic.
    /// </remarks>
    private HashSet<string> Combine(SetOperation operation, string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0) return new HashSet<string>(StringComparer.Ordinal);

        var result = SetMembers(keys[0]);
        for (int i = 1; i < keys.Length; i++)
        {
            var other = SetMembers(keys[i]);
            switch (operation)
            {
                case SetOperation.Union: result.UnionWith(other); break;
                case SetOperation.Intersect: result.IntersectWith(other); break;
                case SetOperation.Difference: result.ExceptWith(other); break;
            }
            if (result.Count == 0 && operation != SetOperation.Union) break;
        }
        return result;
    }
}
