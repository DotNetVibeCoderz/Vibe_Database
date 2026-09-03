namespace MemSharp.Collections;

/// <summary>One member of a sorted set, ordered by score then by member ordinal.</summary>
/// <remarks>
/// <see cref="Edge"/> is what makes range queries cheap. A boundary value needs to sort strictly
/// before or after every real member sharing its score, and no member string can do that reliably.
/// A low sentinel carries -1, a high sentinel +1, and real members 0.
/// </remarks>
internal readonly struct ZEntry : IComparable<ZEntry>
{
    public readonly double Score;
    public readonly string Member;
    public readonly sbyte Edge;

    public ZEntry(double score, string member)
    {
        Score = score;
        Member = member;
        Edge = 0;
    }

    private ZEntry(double score, sbyte edge)
    {
        Score = score;
        Member = string.Empty;
        Edge = edge;
    }

    public static ZEntry LowSentinel(double score) => new(score, -1);
    public static ZEntry HighSentinel(double score) => new(score, 1);

    public int CompareTo(ZEntry other)
    {
        int byScore = Score.CompareTo(other.Score);
        if (byScore != 0) return byScore;
        int byEdge = Edge.CompareTo(other.Edge);
        if (byEdge != 0) return byEdge;
        return string.CompareOrdinal(Member, other.Member);
    }
}

/// <summary>A member with its score, as returned by range queries.</summary>
public readonly record struct ScoredMember(string Member, double Score);

/// <summary>
/// A sorted set: a member-to-score map paired with a score-ordered tree over the same members.
/// </summary>
/// <remarks>
/// Redis uses a skip list here. A red-black tree (<see cref="SortedSet{T}"/>) gives the same
/// O(log n) insert, delete and score-range seek with a fraction of the code, and
/// <see cref="SortedSet{T}.GetViewBetween"/> makes <c>ZRANGEBYSCORE</c> a seek plus a walk of only
/// the matching elements. The trade-off is rank: <c>ZRANK</c> and index-based <c>ZRANGE</c> have to
/// count from one end, so they are O(n) rather than the skip list's O(log n). Order-book depth
/// queries - the workload this was built for - are score ranges and top-N, both of which stay
/// logarithmic.
///
/// Not thread-safe. Callers hold the owning shard's lock.
/// </remarks>
internal sealed class SortedSetStore
{
    private readonly Dictionary<string, double> _scores = new(StringComparer.Ordinal);
    private readonly SortedSet<ZEntry> _ordered = new();

    public int Count => _scores.Count;

    /// <summary>Inserts or rescores a member. Returns true if the member is new.</summary>
    public bool Add(string member, double score)
    {
        if (_scores.TryGetValue(member, out double existing))
        {
            if (existing.Equals(score)) return false;
            _ordered.Remove(new ZEntry(existing, member));
            _scores[member] = score;
            _ordered.Add(new ZEntry(score, member));
            return false;
        }

        _scores.Add(member, score);
        _ordered.Add(new ZEntry(score, member));
        return true;
    }

    public bool Remove(string member)
    {
        if (!_scores.Remove(member, out double score)) return false;
        _ordered.Remove(new ZEntry(score, member));
        return true;
    }

    public bool TryGetScore(string member, out double score) => _scores.TryGetValue(member, out score);

    public double IncrementBy(string member, double delta)
    {
        double updated = _scores.TryGetValue(member, out double current) ? current + delta : delta;
        Add(member, updated);
        return updated;
    }

    /// <summary>Members ordered by rank, with negative indices counting back from the end.</summary>
    public List<ScoredMember> RangeByRank(int start, int stop, bool descending)
    {
        int count = _scores.Count;
        Normalise(ref start, ref stop, count);
        var result = new List<ScoredMember>();
        if (start > stop || count == 0) return result;

        int index = 0;
        foreach (var entry in descending ? _ordered.Reverse() : _ordered)
        {
            if (index > stop) break;
            if (index >= start) result.Add(new ScoredMember(entry.Member, entry.Score));
            index++;
        }
        return result;
    }

    /// <summary>Members whose score falls in the inclusive range, ordered by score.</summary>
    public List<ScoredMember> RangeByScore(double min, double max, bool descending, int offset, int limit)
    {
        var result = new List<ScoredMember>();
        if (_scores.Count == 0 || min > max) return result;

        var view = _ordered.GetViewBetween(ZEntry.LowSentinel(min), ZEntry.HighSentinel(max));
        int skipped = 0;
        foreach (var entry in descending ? view.Reverse() : view)
        {
            if (skipped++ < offset) continue;
            result.Add(new ScoredMember(entry.Member, entry.Score));
            if (limit >= 0 && result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>Zero-based position of a member, or -1 if absent.</summary>
    public int Rank(string member, bool descending)
    {
        if (!_scores.TryGetValue(member, out double score)) return -1;
        var target = new ZEntry(score, member);

        int rank = 0;
        foreach (var entry in descending ? _ordered.Reverse() : _ordered)
        {
            if (entry.CompareTo(target) == 0) return rank;
            rank++;
        }
        return -1;
    }

    public int CountInScoreRange(double min, double max)
    {
        if (_scores.Count == 0 || min > max) return 0;
        return _ordered.GetViewBetween(ZEntry.LowSentinel(min), ZEntry.HighSentinel(max)).Count;
    }

    public IEnumerable<ScoredMember> All()
    {
        foreach (var entry in _ordered) yield return new ScoredMember(entry.Member, entry.Score);
    }

    private static void Normalise(ref int start, ref int stop, int count)
    {
        if (start < 0) start = Math.Max(0, count + start);
        if (stop < 0) stop = count + stop;
        if (stop >= count) stop = count - 1;
    }
}
