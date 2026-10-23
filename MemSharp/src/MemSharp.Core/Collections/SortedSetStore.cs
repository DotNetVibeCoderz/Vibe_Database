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

/// <summary>How <c>ZUNIONSTORE</c> and <c>ZINTERSTORE</c> combine one member's scores from several sources.</summary>
public enum ScoreAggregate
{
    /// <summary>Add them. The default.</summary>
    Sum,

    /// <summary>Keep the lowest.</summary>
    Min,

    /// <summary>Keep the highest.</summary>
    Max,
}

/// <summary>
/// A sorted set: a member-to-score map paired with an order-statistic tree over the same members.
/// </summary>
/// <remarks>
/// <para>
/// The tree is an AVL tree whose every node also records the size of its subtree. Ordering alone
/// gives O(log n) insert, delete and score seek; the sizes are what make <em>rank</em> logarithmic
/// too. To find a member's rank, descend towards it adding up the left subtrees passed on the way;
/// to find the member at rank k, descend steering by the same sizes.
/// </para>
/// <para>
/// 1.0 used <see cref="SortedSet{T}"/>, which knows ordering but not position, so <c>ZRANK</c> and
/// index-based <c>ZRANGE</c> counted from one end - O(n), and the one place MemSharp was worse than
/// Redis on complexity rather than constants. <c>SortedSetRankBenchmarks</c> measures the
/// difference: at a million members, the rank of the middle member went from milliseconds to well
/// under a microsecond. A side effect is <c>ZCOUNT</c>, now two rank lookups subtracted rather than
/// a walk of every match.
/// </para>
/// <para>
/// AVL rather than red-black because it is the shorter correct implementation: the rebalancing is
/// four rotations with no colour cases, and the stricter balance keeps reads - the common
/// operation here - marginally shallower. Redis uses a skip list for the same job; a tree avoids
/// the random level choice and its worst cases.
/// </para>
/// <para>Not thread-safe. Callers hold the owning shard's lock.</para>
/// </remarks>
internal sealed class SortedSetStore
{
    private sealed class Node(ZEntry item)
    {
        public ZEntry Item = item;
        public Node? Left;
        public Node? Right;
        public int Size = 1;
        public sbyte Height = 1;
    }

    private readonly Dictionary<string, double> _scores = new(StringComparer.Ordinal);
    private Node? _root;

    public int Count => _scores.Count;

    /// <summary>Inserts or rescores a member. Returns true if the member is new.</summary>
    public bool Add(string member, double score)
    {
        // A NaN compares unequal to everything, itself included, and would corrupt the tree's order.
        if (double.IsNaN(score)) throw new MemSharpCommandException("score is not a number (NaN)");
        if (_scores.TryGetValue(member, out double existing))
        {
            if (existing.Equals(score)) return false;
            _root = Delete(_root, new ZEntry(existing, member));
            _scores[member] = score;
            _root = Insert(_root, new ZEntry(score, member));
            return false;
        }

        _scores.Add(member, score);
        _root = Insert(_root, new ZEntry(score, member));
        return true;
    }

    public bool Remove(string member)
    {
        if (!_scores.Remove(member, out double score)) return false;
        _root = Delete(_root, new ZEntry(score, member));
        return true;
    }

    public bool TryGetScore(string member, out double score) => _scores.TryGetValue(member, out score);

    public double IncrementBy(string member, double delta)
    {
        double updated = _scores.TryGetValue(member, out double current) ? current + delta : delta;
        if (double.IsNaN(updated)) throw new MemSharpCommandException("resulting score is not a number (NaN)");
        Add(member, updated);
        return updated;
    }

    /// <summary>Members ordered by rank, with negative indices counting back from the end. O(log n + k).</summary>
    public List<ScoredMember> RangeByRank(int start, int stop, bool descending)
    {
        int count = Count;
        Normalise(ref start, ref stop, count);
        var result = new List<ScoredMember>();
        if (start > stop || start >= count) return result;

        // Ranks count from the requested end, so a descending start of k is ascending position
        // count - 1 - k.
        int position = descending ? count - 1 - start : start;
        int wanted = stop - start + 1;
        foreach (var entry in Walk(position, descending))
        {
            result.Add(new ScoredMember(entry.Member, entry.Score));
            if (result.Count == wanted) break;
        }
        return result;
    }

    /// <summary>Members whose score falls in the inclusive range, ordered by score. O(log n + offset + k).</summary>
    public List<ScoredMember> RangeByScore(double min, double max, bool descending, int offset, int limit)
    {
        var result = new List<ScoredMember>();
        if (Count == 0 || min > max) return result;

        var low = ZEntry.LowSentinel(min);
        var high = ZEntry.HighSentinel(max);
        int first = CountBelow(low);
        int last = CountBelow(high) - 1;   // ascending position of the last member <= max
        if (first > last) return result;

        int matches = last - first + 1;
        if (offset >= matches) return result;

        // Skipping by position rather than by walking: the offset costs a descent, not a scan.
        int position = descending ? last - offset : first + offset;
        int remaining = matches - offset;
        if (limit >= 0) remaining = Math.Min(remaining, limit);

        foreach (var entry in Walk(position, descending))
        {
            if (remaining-- == 0) break;
            result.Add(new ScoredMember(entry.Member, entry.Score));
        }
        return result;
    }

    /// <summary>Zero-based position of a member, or -1 if absent. O(log n).</summary>
    public int Rank(string member, bool descending)
    {
        if (!_scores.TryGetValue(member, out double score)) return -1;
        int ascending = CountBelow(new ZEntry(score, member));
        return descending ? Count - 1 - ascending : ascending;
    }

    /// <summary>Members whose score falls in the inclusive range. O(log n).</summary>
    public int CountInScoreRange(double min, double max)
    {
        if (Count == 0 || min > max) return 0;
        return CountBelow(ZEntry.HighSentinel(max)) - CountBelow(ZEntry.LowSentinel(min));
    }

    public IEnumerable<ScoredMember> All()
    {
        if (_root is null) yield break;
        foreach (var entry in Walk(0, descending: false)) yield return new ScoredMember(entry.Member, entry.Score);
    }

    /// <summary>Bytes held: the member map, one tree node per member, and member strings sampled from <paramref name="samples"/>.</summary>
    public long EstimateBytes(int samples) =>
        (2 * MemoryEstimate.Header) + 96 + (Count * (MemoryEstimate.MapEntry + 64)) +
        MemoryEstimate.Extrapolate(_scores.Keys, Count, samples, MemoryEstimate.Of);

    /// <summary>Checks every structural invariant. For tests; O(n).</summary>
    internal void Validate()
    {
        int counted = Check(_root, out _);
        if (counted != _scores.Count) throw new InvalidOperationException($"tree holds {counted}, map holds {_scores.Count}");

        static int Check(Node? node, out int height)
        {
            if (node is null) { height = 0; return 0; }
            int left = Check(node.Left, out int leftHeight);
            int right = Check(node.Right, out int rightHeight);
            if (node.Left is not null && node.Left.Item.CompareTo(node.Item) >= 0) throw new InvalidOperationException("left child out of order");
            if (node.Right is not null && node.Right.Item.CompareTo(node.Item) <= 0) throw new InvalidOperationException("right child out of order");
            if (Math.Abs(leftHeight - rightHeight) > 1) throw new InvalidOperationException("unbalanced");
            height = 1 + Math.Max(leftHeight, rightHeight);
            if (node.Height != height) throw new InvalidOperationException("stale height");
            if (node.Size != left + right + 1) throw new InvalidOperationException("stale size");
            return node.Size;
        }
    }

    // ---- order statistics -----------------------------------------------------------------------

    /// <summary>How many members sort strictly before <paramref name="key"/>.</summary>
    private int CountBelow(ZEntry key)
    {
        int below = 0;
        var node = _root;
        while (node is not null)
        {
            int comparison = key.CompareTo(node.Item);
            if (comparison <= 0)
            {
                if (comparison == 0) return below + SizeOf(node.Left);
                node = node.Left;
            }
            else
            {
                below += SizeOf(node.Left) + 1;
                node = node.Right;
            }
        }
        return below;
    }

    /// <summary>
    /// In-order walk starting at an ascending <paramref name="position"/>, in either direction.
    /// </summary>
    /// <remarks>
    /// No parent pointers, so the walk keeps the path as an explicit stack. The descent that finds
    /// the starting position pushes exactly the ancestors still to be visited in the walk's
    /// direction - the ones it turned away from - so the stack is ready as soon as the seek ends.
    /// The tree must not change while a walk is live; callers materialise before mutating.
    /// </remarks>
    private IEnumerable<ZEntry> Walk(int position, bool descending)
    {
        var stack = new Stack<Node>();
        var node = _root;
        while (node is not null)
        {
            int leftSize = SizeOf(node.Left);
            if (position < leftSize)
            {
                if (!descending) stack.Push(node);
                node = node.Left;
            }
            else if (position == leftSize)
            {
                stack.Push(node);
                break;
            }
            else
            {
                if (descending) stack.Push(node);
                position -= leftSize + 1;
                node = node.Right;
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current.Item;

            // The next node in the walk's direction is the extreme of the subtree on that side.
            var next = descending ? current.Left : current.Right;
            while (next is not null)
            {
                stack.Push(next);
                next = descending ? next.Right : next.Left;
            }
        }
    }

    // ---- AVL maintenance ------------------------------------------------------------------------

    private static int SizeOf(Node? node) => node?.Size ?? 0;
    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static void Update(Node node)
    {
        node.Size = 1 + SizeOf(node.Left) + SizeOf(node.Right);
        node.Height = (sbyte)(1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right)));
    }

    private static Node RotateRight(Node top)
    {
        var pivot = top.Left!;
        top.Left = pivot.Right;
        pivot.Right = top;
        Update(top);
        Update(pivot);
        return pivot;
    }

    private static Node RotateLeft(Node top)
    {
        var pivot = top.Right!;
        top.Right = pivot.Left;
        pivot.Left = top;
        Update(top);
        Update(pivot);
        return pivot;
    }

    /// <summary>Restores the size, the height and the AVL balance of one node after a child changed.</summary>
    private static Node Rebalance(Node node)
    {
        Update(node);
        int balance = HeightOf(node.Left) - HeightOf(node.Right);

        if (balance > 1)
        {
            if (HeightOf(node.Left!.Left) < HeightOf(node.Left.Right)) node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            if (HeightOf(node.Right!.Right) < HeightOf(node.Right.Left)) node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }
        return node;
    }

    // Recursive: an AVL tree of a billion members is at most 44 levels deep, so the stack is not a
    // concern, and the recursive form is the one whose correctness can be read off the page.
    private static Node Insert(Node? node, ZEntry item)
    {
        if (node is null) return new Node(item);
        if (item.CompareTo(node.Item) < 0) node.Left = Insert(node.Left, item);
        else node.Right = Insert(node.Right, item);
        return Rebalance(node);
    }

    private static Node? Delete(Node? node, ZEntry item)
    {
        if (node is null) return null;

        int comparison = item.CompareTo(node.Item);
        if (comparison < 0)
        {
            node.Left = Delete(node.Left, item);
        }
        else if (comparison > 0)
        {
            node.Right = Delete(node.Right, item);
        }
        else
        {
            if (node.Left is null) return node.Right;
            if (node.Right is null) return node.Left;

            // Two children: take the successor's item, then delete the successor from the right.
            node.Right = DeleteMin(node.Right, out var successor);
            node.Item = successor;
        }
        return Rebalance(node);
    }

    private static Node? DeleteMin(Node node, out ZEntry min)
    {
        if (node.Left is null)
        {
            min = node.Item;
            return node.Right;
        }
        node.Left = DeleteMin(node.Left, out min);
        return Rebalance(node);
    }

    private static void Normalise(ref int start, ref int stop, int count)
    {
        if (start < 0) start = Math.Max(0, count + start);
        if (stop < 0) stop = count + stop;
        if (stop >= count) stop = count - 1;
    }
}
