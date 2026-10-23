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
            RecordWrite(shard, "ZADD", key, Format(score), member);
        }
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
            if (flattened.Count > 1) RecordWrite(shard, "ZADD", flattened.ToArray());
        }
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
            if (removed > 0) RecordWrite(shard, "ZREM", Prepend(key, members));
        }
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
            RecordWrite(shard, "ZINCRBY", key, Format(delta), member);
        }
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

    /// <summary>
    /// Stores the union of sorted sets (or plain sets, whose members score 1) at
    /// <paramref name="destination"/>, replacing it. Returns the size of the result.
    /// </summary>
    /// <param name="destination">Where the result goes. An empty result deletes it.</param>
    /// <param name="keys">Source keys.</param>
    /// <param name="weights">Per-source score multipliers, or null for all 1.</param>
    /// <param name="aggregate">How a member's scores from several sources combine.</param>
    /// <remarks>
    /// Point-in-time, like <see cref="SetIntersectStore"/>: every involved shard is locked in shard
    /// order for the whole operation, because a stored result is durable.
    /// </remarks>
    public int SortedSetUnionStore(
        string destination, string[] keys, double[]? weights = null, ScoreAggregate aggregate = ScoreAggregate.Sum) =>
        CombineSortedStore(intersect: false, destination, keys, weights, aggregate);

    /// <summary>
    /// Stores the members present in every source at <paramref name="destination"/>. See
    /// <see cref="SortedSetUnionStore"/>.
    /// </summary>
    public int SortedSetIntersectStore(
        string destination, string[] keys, double[]? weights = null, ScoreAggregate aggregate = ScoreAggregate.Sum) =>
        CombineSortedStore(intersect: true, destination, keys, weights, aggregate);

    private int CombineSortedStore(bool intersect, string destination, string[] keys, double[]? weights, ScoreAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0) throw new MemSharpCommandException("at least one source key is required");
        if (weights is not null && weights.Length != keys.Length)
        {
            throw new MemSharpCommandException("WEIGHTS must give one weight per source key");
        }

        var shards = ShardsInOrder(destination, keys);
        long now = NowTicks;

        EnterAll(shards);
        try
        {
            Dictionary<string, double>? result = null;

            for (int i = 0; i < keys.Length; i++)
            {
                double weight = weights?[i] ?? 1;
                var source = ReadScoredLocked(keys[i], now);

                if (result is null)
                {
                    result = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var (member, score) in source) result[member] = Weigh(score, weight);
                    continue;
                }

                if (intersect)
                {
                    var narrowed = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var (member, score) in source)
                    {
                        if (result.TryGetValue(member, out double existing))
                        {
                            narrowed[member] = Aggregate(existing, Weigh(score, weight), aggregate);
                        }
                    }
                    result = narrowed;
                    if (result.Count == 0) break;
                }
                else
                {
                    foreach (var (member, score) in source)
                    {
                        double weighed = Weigh(score, weight);
                        result[member] = result.TryGetValue(member, out double existing)
                            ? Aggregate(existing, weighed, aggregate)
                            : weighed;
                    }
                }
            }

            SortedSetStore? stored = null;
            var logged = new List<string>(1 + (result!.Count * 2)) { destination };
            if (result.Count > 0)
            {
                stored = new SortedSetStore();
                foreach (var (member, score) in result)
                {
                    stored.Add(member, score);
                    logged.Add(Format(score));
                    logged.Add(member);
                }
            }

            var destinationShard = ShardFor(destination);
            Overwrite(destinationShard, destination, MemType.SortedSet, stored);

            // The result, not the command - see CombineStore in MemDb.Sets.cs for why.
            RecordWrite(destinationShard, "DEL", destination);
            if (stored is not null) RecordWrite(destinationShard, "ZADD", logged.ToArray());
            return result.Count;
        }
        finally
        {
            ExitAll(shards);
        }
    }

    /// <summary>
    /// A source for the sorted-set algebra: a sorted set's members with their scores, or a plain
    /// set's members scored 1, as Redis does. Absent keys are empty. Caller holds the key's gate.
    /// </summary>
    private List<(string Member, double Score)> ReadScoredLocked(string key, long now)
    {
        var shard = ShardFor(key);
        var members = new List<(string, double)>();
        if (!TryGetLive(shard, key, now, out var entry)) return members;

        switch (entry.Type)
        {
            case MemType.SortedSet:
                foreach (var member in ((SortedSetStore)entry.Value).All()) members.Add((member.Member, member.Score));
                break;
            case MemType.Set:
                foreach (var member in (HashSet<string>)entry.Value) members.Add((member, 1));
                break;
            default:
                throw new WrongTypeException(key, entry.Type, MemType.SortedSet);
        }
        return members;
    }

    // Infinity times zero is NaN, and a NaN score would poison the tree's ordering. Redis defines
    // the product as 0 in that case; so does this.
    private static double Weigh(double score, double weight)
    {
        double product = score * weight;
        return double.IsNaN(product) ? 0 : product;
    }

    private static double Aggregate(double a, double b, ScoreAggregate how)
    {
        double combined = how switch
        {
            ScoreAggregate.Min => Math.Min(a, b),
            ScoreAggregate.Max => Math.Max(a, b),
            _ => a + b,
        };
        return double.IsNaN(combined) ? 0 : combined;   // +inf plus -inf
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

            if (removed > 0) RecordWrite(shard, "ZREMRANGEBYSCORE", key, Format(min), Format(max));
        }
        return removed;
    }
}
