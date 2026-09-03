using System.Globalization;
using MemSharp.Collections;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Adds a member or updates its score. Returns true if the member is new.</summary>
    public bool SortedSetAdd(string key, string member, double score)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);

        var shard = ShardFor(key);
        long now = NowTicks;
        bool added;

        lock (shard.Gate)
        {
            var set = GetOrCreate(shard, key, MemType.SortedSet, now, static () => new SortedSetStore());
            added = set.Add(member, score);
        }

        RecordWrite("ZADD", key, Format(score), member);
        return added;
    }

    /// <summary>Adds several scored members at once. Returns how many were new.</summary>
    public int SortedSetAdd(string key, IEnumerable<ScoredMember> members)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(members);

        var shard = ShardFor(key);
        long now = NowTicks;
        int added = 0;
        var flattened = new List<string> { key };

        lock (shard.Gate)
        {
            var set = GetOrCreate(shard, key, MemType.SortedSet, now, static () => new SortedSetStore());
            foreach (var entry in members)
            {
                if (set.Add(entry.Member, entry.Score)) added++;
                flattened.Add(Format(entry.Score));
                flattened.Add(entry.Member);
            }
        }

        if (flattened.Count > 1) RecordWrite("ZADD", flattened.ToArray());
        return added;
    }

    /// <summary>Removes members. Returns how many were present.</summary>
    public int SortedSetRemove(string key, params string[] members)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(members);

        var shard = ShardFor(key);
        long now = NowTicks;
        int removed = 0;

        lock (shard.Gate)
        {
            if (!TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)) return 0;
            foreach (var member in members)
            {
                if (set.Remove(member)) removed++;
            }
            RemoveIfEmpty(shard, key, set.Count);
        }

        if (removed > 0) RecordWrite("ZREM", Prepend(key, members));
        return removed;
    }

    /// <summary>A member's score, or <c>null</c> if it is absent.</summary>
    public double? SortedSetScore(string key, string member)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set) &&
                set.TryGetScore(member, out double score))
            {
                return score;
            }
            return null;
        }
    }

    /// <summary>Atomically adds to a member's score, creating it at <paramref name="delta"/> if absent.</summary>
    public double SortedSetIncrement(string key, string member, double delta)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);

        var shard = ShardFor(key);
        long now = NowTicks;
        double updated;

        lock (shard.Gate)
        {
            var set = GetOrCreate(shard, key, MemType.SortedSet, now, static () => new SortedSetStore());
            updated = set.IncrementBy(member, delta);
        }

        RecordWrite("ZINCRBY", key, Format(delta), member);
        return updated;
    }

    /// <summary>
    /// Members in an inclusive rank range, lowest score first unless <paramref name="descending"/>.
    /// Negative indices count back from the end.
    /// </summary>
    /// <remarks>
    /// O(stop) - ranks are counted by walking the tree, not indexed. Prefer
    /// <see cref="SortedSetRangeByScore"/> when the bound is a score, which seeks in O(log n).
    /// Top-N queries, where <paramref name="stop"/> is small, stay cheap either way.
    /// </remarks>
    public List<ScoredMember> SortedSetRangeByRank(string key, int start, int stop, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)
                ? set.RangeByRank(start, stop, descending)
                : new List<ScoredMember>();
        }
    }

    /// <summary>
    /// Members whose score falls in an inclusive range. Seeks to the boundary in O(log n) and walks
    /// only the matches.
    /// </summary>
    /// <param name="key">The sorted set.</param>
    /// <param name="min">Lowest score, inclusive.</param>
    /// <param name="max">Highest score, inclusive.</param>
    /// <param name="descending">Return highest score first.</param>
    /// <param name="offset">Matches to skip.</param>
    /// <param name="limit">Maximum matches to return, or -1 for all of them.</param>
    public List<ScoredMember> SortedSetRangeByScore(
        string key, double min, double max, bool descending = false, int offset = 0, int limit = -1)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)
                ? set.RangeByScore(min, max, descending, offset, limit)
                : new List<ScoredMember>();
        }
    }

    /// <summary>Zero-based rank of a member, or <c>null</c> if it is absent.</summary>
    public int? SortedSetRank(string key, string member, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)) return null;
            int rank = set.Rank(member, descending);
            return rank < 0 ? null : rank;
        }
    }

    /// <summary>Number of members, or 0 if the key is absent.</summary>
    public int SortedSetLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set) ? set.Count : 0;
        }
    }

    /// <summary>Number of members whose score falls in an inclusive range.</summary>
    public int SortedSetCountByScore(string key, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)
                ? set.CountInScoreRange(min, max)
                : 0;
        }
    }

    /// <summary>Removes every member whose score falls in an inclusive range. Returns how many went.</summary>
    public int SortedSetRemoveByScore(string key, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        int removed = 0;

        lock (shard.Gate)
        {
            if (!TryGetTyped<SortedSetStore>(shard, key, MemType.SortedSet, now, out var set)) return 0;

            // Materialise the doomed members before removing: the range view is a live projection of
            // the tree, and mutating the tree while enumerating it would invalidate the enumerator.
            var doomed = set.RangeByScore(min, max, descending: false, offset: 0, limit: -1);
            foreach (var entry in doomed)
            {
                if (set.Remove(entry.Member)) removed++;
            }
            RemoveIfEmpty(shard, key, set.Count);
        }

        if (removed > 0)
        {
            RecordWrite("ZREMRANGEBYSCORE", key,
                min.ToString("R", CultureInfo.InvariantCulture),
                max.ToString("R", CultureInfo.InvariantCulture));
        }
        return removed;
    }
}
