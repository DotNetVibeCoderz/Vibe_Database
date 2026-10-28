using Faiss.Net.Utils;

namespace Faiss.Net.Core;

/// <summary>
/// Distances as the HNSW graph sees them: always smaller-is-better, so one traversal serves every
/// metric (inner product is negated by the implementation). Implemented by structs, consumed
/// through generic constraints, so the JIT specializes the whole graph algorithm per storage type
/// and the distance call inlines instead of going through a virtual dispatch per candidate.
/// </summary>
internal interface IHnswComputer
{
    /// <summary>Score from the computer's query to a stored node.</summary>
    float Query(int node);

    /// <summary>Score between two stored nodes, used by the diversity heuristic.</summary>
    float Pair(int a, int b);
}

/// <summary>Per-traversal working set, pooled so a query allocates nothing after warm-up.</summary>
internal sealed class HnswScratch
{
    public readonly VisitedTable Visited = new(1024);
    public readonly PriorityQueue<int, float> Candidates = new();
    public readonly PriorityQueue<int, float> Results = new();

    private float[] _a = [], _b = [], _q = [], _table = [];

    /// <summary>
    /// Free buffers for storage that must decode a node or build a lookup table per query. They live
    /// on the pinned object heap, so a distance computer can hold raw pointers into them for as long
    /// as the scratch is rented, without a <c>fixed</c> block around every call.
    /// </summary>
    public unsafe float* A(int length) => Pin(ref _a, length);

    /// <inheritdoc cref="A"/>
    public unsafe float* B(int length) => Pin(ref _b, length);

    /// <summary>Buffer holding a decoded query, for storages that cannot score a code against a code.</summary>
    public unsafe float* Q(int length) => Pin(ref _q, length);

    /// <summary>Buffer holding a per-query lookup table.</summary>
    public unsafe float* Table(int length) => Pin(ref _table, length);

    /// <summary>The table buffer as a span, for code that fills it through a span API.</summary>
    public Span<float> TableSpan(int length) => _table.AsSpan(0, length);

    private static unsafe float* Pin(ref float[] buffer, int length)
    {
        if (buffer.Length < length) buffer = GC.AllocateUninitializedArray<float>(length, pinned: true);
        return (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(buffer));
    }
}

/// <summary>
/// The HNSW algorithms — greedy descent, beam search on one layer, the diversity heuristic and
/// insertion — written once over any <see cref="IHnswComputer"/>.
/// </summary>
internal static class HnswAlgorithm
{
    /// <summary>Walks downhill on one layer until no neighbour improves on the current node.</summary>
    public static void GreedyDescend<T>(HnswGraph graph, ref T dc, ref int entry, ref float entryScore, int level, int limit)
        where T : struct, IHnswComputer
    {
        bool improved = true;
        while (improved)
        {
            improved = false;
            var neighbors = graph.Neighbors(entry, level);
            for (int i = 0; i < neighbors.Length; i++)
            {
                int candidate = neighbors[i];
                if (candidate == HnswGraph.NoNeighbor) break;
                if (candidate >= limit) continue; // linked by an insert this traversal cannot see
                float score = dc.Query(candidate);
                if (score < entryScore)
                {
                    entryScore = score;
                    entry = candidate;
                    improved = true;
                }
            }
        }
    }

    /// <summary>
    /// Best-first exploration of one layer, keeping the <paramref name="ef"/> closest nodes seen.
    /// Stops as soon as the nearest unexplored candidate is worse than the current worst result —
    /// the bound that keeps the walk logarithmic instead of exhaustive.
    /// <para>
    /// With a selector, every node is still <em>explored</em> — the graph must stay navigable through
    /// non-members — but only members enter the result set. The result set therefore fills more
    /// slowly and the stopping bound loosens, which is what widens the walk under a selective filter
    /// instead of returning a short list.
    /// </para>
    /// </summary>
    public static List<(int Node, float Score)> SearchLayer<T>(
        HnswGraph graph, ref T dc, int entry, float entryScore, int ef, int level, HnswScratch scratch,
        int limit, int exclude = -1, IDSelector? sel = null)
        where T : struct, IHnswComputer
    {
        var visited = scratch.Visited;
        var candidates = scratch.Candidates;
        var results = scratch.Results;

        visited.Reset(limit);
        candidates.Clear();
        results.Clear();

        visited.Visit(entry);
        candidates.Enqueue(entry, entryScore);
        if (entry != exclude && (sel is null || sel.IsMember(entry))) results.Enqueue(entry, -entryScore);

        while (candidates.TryPeek(out int current, out float currentScore))
        {
            if (results.Count >= ef && results.TryPeek(out _, out float worstNegated) && currentScore > -worstNegated)
                break;
            candidates.Dequeue();

            var neighbors = graph.Neighbors(current, level);
            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighbor = neighbors[i];
                if (neighbor == HnswGraph.NoNeighbor) break;
                if (neighbor >= limit) continue; // linked by an insert this traversal cannot see
                if (!visited.Visit(neighbor)) continue;

                float score = dc.Query(neighbor);
                bool worthKeeping = results.Count < ef ||
                                    (results.TryPeek(out _, out float worst) && score < -worst);
                if (!worthKeeping) continue;

                candidates.Enqueue(neighbor, score);
                if (neighbor == exclude) continue;
                if (sel is not null && !sel.IsMember(neighbor)) continue;
                results.Enqueue(neighbor, -score);
                if (results.Count > ef) results.Dequeue();
            }
        }

        var output = new List<(int, float)>(results.Count);
        while (results.TryDequeue(out int node, out float negated)) output.Add((node, -negated));
        return output;
    }

    /// <summary>Full search: descend the upper layers, then a beam search of layer 0, sorted best first.</summary>
    public static List<(int Node, float Score)> Search<T>(
        HnswGraph graph, ref T dc, int entry, int maxLevel, int ef, HnswScratch scratch, int limit, IDSelector? sel)
        where T : struct, IHnswComputer
    {
        float entryScore = dc.Query(entry);
        for (int level = maxLevel; level > 0; level--)
            GreedyDescend(graph, ref dc, ref entry, ref entryScore, level, limit);

        var found = SearchLayer(graph, ref dc, entry, entryScore, ef, 0, scratch, limit, -1, sel);
        found.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        return found;
    }

    /// <summary>
    /// HNSW's diversity heuristic: keep a candidate only if it is closer to the query than to any
    /// already-selected neighbour.
    /// <para>
    /// Taking simply the M nearest would fill a node's links with a single tight cluster, leaving
    /// whole regions unreachable. Preferring neighbours that point in genuinely different directions
    /// is what keeps the graph navigable, and it matters far more to recall than raw degree.
    /// </para>
    /// <para>
    /// Candidates the heuristic rejects are then used to back-fill up to <paramref name="m"/> links
    /// (the paper's <c>keepPrunedConnections</c>). This is not optional in practice: in high
    /// dimension all pairwise distances concentrate, so the diversity test rejects roughly half of
    /// what it sees at every step and degree collapses exponentially. Without the back-fill a graph
    /// built with M=32 ends up averaging around 16 links per node and recall falls by tens of points.
    /// </para>
    /// </summary>
    public static List<int> SelectNeighbors<T>(ref T dc, List<(int Node, float Score)> candidates, int m)
        where T : struct, IHnswComputer
    {
        var selected = new List<int>(m);
        List<int>? pruned = null;

        foreach (var (node, score) in candidates)
        {
            if (selected.Count >= m) break;

            bool diverse = true;
            foreach (int chosen in selected)
            {
                if (dc.Pair(node, chosen) < score)
                {
                    diverse = false;
                    break;
                }
            }

            if (diverse) selected.Add(node);
            else (pruned ??= []).Add(node);
        }

        // Back-fill in distance order; `candidates` is sorted, so `pruned` already is too.
        if (pruned is not null)
            for (int i = 0; i < pruned.Count && selected.Count < m; i++)
                selected.Add(pruned[i]);

        return selected;
    }

    /// <summary>Writes the node's links and adds the reverse link on each neighbour, pruning if full.</summary>
    public static void Connect<T>(HnswGraph graph, ref T dc, int node, int level, List<int> selected, int maxDegree)
        where T : struct, IHnswComputer
    {
        lock (graph.LockFor(node))
        {
            var slots = graph.Neighbors(node, level);
            for (int i = 0; i < slots.Length; i++)
                slots[i] = i < selected.Count ? selected[i] : HnswGraph.NoNeighbor;
        }

        foreach (int neighbor in selected)
        {
            lock (graph.LockFor(neighbor))
            {
                var slots = graph.Neighbors(neighbor, level);
                int free = -1;
                bool alreadyLinked = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == node) { alreadyLinked = true; break; }
                    if (slots[i] == HnswGraph.NoNeighbor) { free = i; break; }
                }
                if (alreadyLinked) continue;

                if (free >= 0)
                {
                    slots[free] = node;
                    continue;
                }

                // Full: re-run the heuristic over the existing neighbours plus the new one, so the
                // link that gets dropped is a redundant one rather than simply the newest.
                var candidates = new List<(int, float)>(slots.Length + 1);
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != HnswGraph.NoNeighbor)
                        candidates.Add((slots[i], dc.Pair(neighbor, slots[i])));
                candidates.Add((node, dc.Pair(neighbor, node)));
                candidates.Sort(static (a, b) => a.Item2.CompareTo(b.Item2));

                var pruned = SelectNeighbors(ref dc, candidates, maxDegree);
                for (int i = 0; i < slots.Length; i++)
                    slots[i] = i < pruned.Count ? pruned[i] : HnswGraph.NoNeighbor;
            }
        }
    }

    /// <summary>
    /// Inserts one node, whose vector is the computer's query: descend to its level, then connect on
    /// every layer down to 0. A node that raises the graph's height becomes the new entry point, and
    /// that promotion is exclusive and happens only after the node is linked, so a concurrent insert
    /// can never start its descent from a node that is not yet reachable on the new top layer.
    /// </summary>
    public static void Insert<T>(HnswGraph graph, ref T dc, int node, int nodeLevel, int efConstruction,
        HnswScratch scratch, object entryPointLock)
        where T : struct, IHnswComputer
    {
        if (nodeLevel > graph.MaxLevel)
        {
            lock (entryPointLock)
            {
                if (nodeLevel > graph.MaxLevel)
                {
                    ConnectNode(graph, ref dc, node, nodeLevel, efConstruction, scratch);
                    graph.SetEntryPoint(node, nodeLevel);
                    return;
                }
            }
        }
        ConnectNode(graph, ref dc, node, nodeLevel, efConstruction, scratch);
    }

    private static void ConnectNode<T>(HnswGraph graph, ref T dc, int node, int nodeLevel, int efConstruction,
        HnswScratch scratch)
        where T : struct, IHnswComputer
    {
        var (entry, maxLevel) = graph.Entry;
        if (entry < 0 || entry == node) return;
        int limit = graph.Count;

        float entryScore = dc.Query(entry);
        for (int level = maxLevel; level > nodeLevel; level--)
            GreedyDescend(graph, ref dc, ref entry, ref entryScore, level, limit);

        for (int level = Math.Min(nodeLevel, maxLevel); level >= 0; level--)
        {
            var candidates = SearchLayer(graph, ref dc, entry, entryScore, efConstruction, level, scratch, limit, node);
            if (candidates.Count == 0) continue;

            candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));
            entry = candidates[0].Node;
            entryScore = candidates[0].Score;

            // The new node takes at most M links of its own; an existing node may accumulate
            // up to M0 on layer 0 through reverse links before it is pruned back.
            int maxDegree = level == 0 ? graph.M0 : graph.M;
            var selected = SelectNeighbors(ref dc, candidates, graph.M);
            Connect(graph, ref dc, node, level, selected, maxDegree);
        }
    }
}

/// <summary>Batch insertion shared by the float and binary HNSW indexes.</summary>
internal static class HnswBuild
{
    /// <summary>
    /// Links nodes <c>first .. first + levels.Length</c>, already stored and already reserved in the
    /// graph. The first node of an empty graph becomes the entry point; the rest are linked in
    /// parallel unless the batch is small.
    /// </summary>
    public static void InsertAll(HnswGraph graph, HnswSession session, int first, int[] levels, int efConstruction,
        int threads, object entryPointLock, Func<HnswScratch> rent, Action<HnswScratch> release)
    {
        int n = levels.Length;
        if (graph.EntryPoint < 0) graph.SetEntryPoint(first, levels[0]);
        int start = graph.EntryPoint == first ? 1 : 0;

        if (threads == 1 || n < 64)
        {
            var scratch = rent();
            for (int i = start; i < n; i++)
                session.Insert(graph, first + i, levels[i], efConstruction, scratch, entryPointLock);
            release(scratch);
            return;
        }

        Parallel.For(start, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var scratch = rent();
            session.Insert(graph, first + i, levels[i], efConstruction, scratch, entryPointLock);
            release(scratch);
        });
    }
}

/// <summary>
/// One pinned view of an HNSW index's storage, opened for a build or for a batch of searches.
/// The non-generic face is what <see cref="IndexHNSW"/> talks to; the generic subclass below is
/// where the algorithm gets specialized for a concrete storage.
/// </summary>
internal abstract unsafe class HnswSession : IDisposable
{
    /// <summary>Nodes this session can see. Links to nodes at or beyond it are ignored.</summary>
    public int Limit { get; protected init; }

    public abstract void Insert(HnswGraph graph, int node, int level, int efConstruction, HnswScratch scratch, object entryPointLock);

    public abstract List<(int Node, float Score)> Search(
        HnswGraph graph, nint query, int entry, int maxLevel, int ef, HnswScratch scratch, IDSelector? sel);

    /// <summary>Exact nearest stored node to a query, by exhaustive scan.</summary>
    public abstract (int Node, float Score) ExactNearest(nint query, HnswScratch scratch);

    /// <summary>Score from a query to one node.</summary>
    public abstract float Score(nint query, int node, HnswScratch scratch);

    /// <summary>Score between two stored nodes.</summary>
    public abstract float Pair(int a, int b, HnswScratch scratch);

    public abstract void Dispose();
}

/// <summary>Session over a concrete storage, with the algorithm specialized for its computer.</summary>
internal abstract unsafe class HnswSession<T> : HnswSession where T : struct, IHnswComputer
{
    /// <summary>A computer whose query is an external vector.</summary>
    protected abstract T ForQuery(nint query, HnswScratch scratch);

    /// <summary>A computer whose query is a stored node, for insertion.</summary>
    protected abstract T ForNode(int node, HnswScratch scratch);

    public override void Insert(HnswGraph graph, int node, int level, int efConstruction, HnswScratch scratch, object entryPointLock)
    {
        var dc = ForNode(node, scratch);
        HnswAlgorithm.Insert(graph, ref dc, node, level, efConstruction, scratch, entryPointLock);
    }

    public override List<(int Node, float Score)> Search(
        HnswGraph graph, nint query, int entry, int maxLevel, int ef, HnswScratch scratch, IDSelector? sel)
    {
        var dc = ForQuery(query, scratch);
        return HnswAlgorithm.Search(graph, ref dc, entry, maxLevel, ef, scratch, Limit, sel);
    }

    public override (int Node, float Score) ExactNearest(nint query, HnswScratch scratch)
    {
        var dc = ForQuery(query, scratch);
        int best = -1;
        float bestScore = float.MaxValue;
        for (int node = 0; node < Limit; node++)
        {
            float score = dc.Query(node);
            if (score < bestScore) { bestScore = score; best = node; }
        }
        return (best, bestScore);
    }

    public override float Score(nint query, int node, HnswScratch scratch)
    {
        var dc = ForQuery(query, scratch);
        return dc.Query(node);
    }

    public override float Pair(int a, int b, HnswScratch scratch)
    {
        var dc = ForNode(a, scratch);
        return dc.Pair(a, b);
    }
}
