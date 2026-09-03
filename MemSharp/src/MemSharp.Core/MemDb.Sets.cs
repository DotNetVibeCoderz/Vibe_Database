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
        }

        RecordWrite("SADD", Prepend(key, members));
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
        }

        if (removed > 0) RecordWrite("SREM", Prepend(key, members));
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
        }

        if (popped is not null) RecordWrite("SREM", key, popped);
        return popped;
    }

    /// <summary>Members present in every named set.</summary>
    public HashSet<string> SetIntersect(params string[] keys) => Combine(SetOperation.Intersect, keys);

    /// <summary>Members present in any named set.</summary>
    public HashSet<string> SetUnion(params string[] keys) => Combine(SetOperation.Union, keys);

    /// <summary>Members of the first set that appear in none of the others.</summary>
    public HashSet<string> SetDifference(params string[] keys) => Combine(SetOperation.Difference, keys);

    private enum SetOperation { Union, Intersect, Difference }

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
