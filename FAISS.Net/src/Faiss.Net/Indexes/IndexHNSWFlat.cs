using System.Collections.Concurrent;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Hierarchical Navigable Small World graph over full-precision vectors — the fastest index here at
/// high recall, and the usual choice when queries must be answered in well under a millisecond.
/// <para>
/// Search is a greedy walk: descend the sparse upper layers to land near the query, then explore
/// layer 0 with a priority queue of width <see cref="EfSearch"/>. Cost grows logarithmically with
/// the database rather than linearly, and unlike IVF there is no training step and no partition to
/// go stale as data is added.
/// </para>
/// <para>
/// The trade is memory and build time: the graph adds roughly <c>4 * (2M + M * levels)</c> bytes per
/// vector on top of the vectors themselves, and construction runs a full search per insertion.
/// <see cref="M"/> and <see cref="EfConstruction"/> are set once at build time; only
/// <see cref="EfSearch"/> can be retuned afterwards.
/// </para>
/// <para>
/// Construction is multi-threaded. Insertions take a per-node lock only while rewriting that node's
/// links, and read neighbour lists without locking: a reader may briefly observe a half-updated
/// list, which can cost one candidate in an approximate search and never corrupts the graph.
/// </para>
/// </summary>
public sealed class IndexHNSWFlat : Index
{
    private readonly VectorStore _store;
    private HnswGraph _graph;
    private readonly RandomGenerator _levelRng;
    private readonly object _entryPointLock = new();
    private readonly ConcurrentBag<Scratch> _scratchPool = [];

    /// <summary>Links per node above layer 0. Higher means better recall, more memory, slower build. FAISS defaults to 32.</summary>
    public int M => _graph.M;

    /// <summary>Search width during construction. Higher builds a better graph at linear build cost.</summary>
    public int EfConstruction { get; set; } = 40;

    /// <summary>
    /// Search width at query time — the recall/latency dial, adjustable at any point after building.
    /// Values below <c>k</c> are raised to <c>k</c>, since the queue must hold the result set.
    /// </summary>
    public int EfSearch { get; set; } = 16;

    /// <summary>The underlying graph, for inspection and diagnostics.</summary>
    public HnswGraph Graph => _graph;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="m">Links per node above layer 0.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexHNSWFlat(int dimension, int m = 32, MetricType metric = MetricType.L2)
        : base(dimension, metric)
    {
        _store = new VectorStore(dimension);
        _graph = new HnswGraph(m);
        _levelRng = new RandomGenerator(1234);
        IsTrained = true;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Raw stored vectors, <c>ntotal * d</c> row-major.</summary>
    public ReadOnlySpan<float> Vectors => _store.AsSpan();

    /// <summary>
    /// Internal ordering score: always smaller-is-better, so one traversal implementation serves
    /// every metric. Inner product is negated on the way in and back out at the API boundary.
    /// </summary>
    private unsafe float Score(float* a, float* b) =>
        MetricType == MetricType.InnerProduct
            ? -VectorOps.InnerProduct(a, b, D)
            : VectorOps.Distance(a, b, D, MetricType);

    private float ToPublicDistance(float score) => MetricType == MetricType.InnerProduct ? -score : score;

    // -------------------------------------------------------------- Adding

    public override unsafe void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        int first = _store.Count;
        _store.Add(x);

        // Levels are drawn serially so a given seed always produces the same graph shape, then all
        // link slots are reserved before any thread starts linking.
        var levels = new int[n];
        for (int i = 0; i < n; i++) levels[i] = _graph.RandomLevel(_levelRng);
        _graph.AddNodes(levels);

        if (_graph.EntryPoint < 0)
        {
            _graph.SetEntryPoint(first, levels[0]);
            LinkNode(first, levels[0]);
        }

        int threads = Threads > 0 ? Threads : Environment.ProcessorCount;
        int start = _graph.EntryPoint == first ? 1 : 0;

        if (threads == 1 || n < 64)
        {
            for (int i = start; i < n; i++) LinkNode(first + i, levels[i]);
        }
        else
        {
            Parallel.For(start, n, new ParallelOptions { MaxDegreeOfParallelism = threads },
                i => LinkNode(first + i, levels[i]));
        }

        Ntotal = _store.Count;
    }

    /// <summary>Inserts one node: descend to its level, then connect on every layer down to 0.</summary>
    private unsafe void LinkNode(int node, int nodeLevel)
    {
        // A node that raises the graph's height becomes the new entry point. That update must be
        // exclusive, otherwise a concurrent insert could start its descent from a node that is not
        // yet linked at the new top layer.
        bool raisesGraph = nodeLevel > _graph.MaxLevel;
        if (raisesGraph)
        {
            lock (_entryPointLock)
            {
                if (nodeLevel > _graph.MaxLevel)
                {
                    ConnectNode(node, nodeLevel);
                    _graph.SetEntryPoint(node, nodeLevel);
                    return;
                }
            }
        }
        ConnectNode(node, nodeLevel);
    }

    private unsafe void ConnectNode(int node, int nodeLevel)
    {
        int entry = _graph.EntryPoint;
        int maxLevel = _graph.MaxLevel;
        if (entry < 0 || entry == node) return;

        var scratch = RentScratch();
        try
        {
            fixed (float* buffer = _store.Buffer)
            {
                float* query = buffer + (long)node * D;
                float entryScore = Score(query, buffer + (long)entry * D);

                // Greedy descent through layers this node does not belong to.
                for (int level = maxLevel; level > nodeLevel; level--)
                    GreedyDescend(buffer, query, ref entry, ref entryScore, level);

                for (int level = Math.Min(nodeLevel, maxLevel); level >= 0; level--)
                {
                    var candidates = SearchLayer(buffer, query, entry, entryScore, EfConstruction, level, scratch, node);
                    if (candidates.Count == 0) continue;

                    candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));
                    entry = candidates[0].Node;
                    entryScore = candidates[0].Score;

                    // The new node takes at most M links of its own; an existing node may accumulate
                    // up to M0 on layer 0 through reverse links before it is pruned back.
                    int maxDegree = level == 0 ? _graph.M0 : _graph.M;
                    var selected = SelectNeighbors(buffer, candidates, _graph.M);
                    Connect(buffer, node, level, selected, maxDegree);
                }
            }
        }
        finally
        {
            _scratchPool.Add(scratch);
        }
    }

    /// <summary>Walks downhill on one layer until no neighbour improves on the current node.</summary>
    private unsafe void GreedyDescend(float* buffer, float* query, ref int entry, ref float entryScore, int level)
    {
        bool improved = true;
        while (improved)
        {
            improved = false;
            var neighbors = _graph.Neighbors(entry, level);
            for (int i = 0; i < neighbors.Length; i++)
            {
                int candidate = neighbors[i];
                if (candidate == HnswGraph.NoNeighbor) break;
                float score = Score(query, buffer + (long)candidate * D);
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
    /// </summary>
    private unsafe List<(int Node, float Score)> SearchLayer(
        float* buffer, float* query, int entry, float entryScore, int ef, int level, Scratch scratch, int exclude = -1)
    {
        var visited = scratch.Visited;
        var candidates = scratch.Candidates;
        var results = scratch.Results;

        visited.Reset(_graph.Count);
        candidates.Clear();
        results.Clear();

        visited.Visit(entry);
        if (entry != exclude)
        {
            candidates.Enqueue(entry, entryScore);
            results.Enqueue(entry, -entryScore);
        }
        else
        {
            candidates.Enqueue(entry, entryScore);
        }

        while (candidates.TryPeek(out int current, out float currentScore))
        {
            if (results.Count >= ef && results.TryPeek(out _, out float worstNegated) && currentScore > -worstNegated)
                break;
            candidates.Dequeue();

            var neighbors = _graph.Neighbors(current, level);
            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighbor = neighbors[i];
                if (neighbor == HnswGraph.NoNeighbor) break;
                if (neighbor >= _graph.Count) continue; // link written by a concurrent insert
                if (!visited.Visit(neighbor)) continue;

                float score = Score(query, buffer + (long)neighbor * D);
                bool worthKeeping = results.Count < ef ||
                                    (results.TryPeek(out _, out float worst) && score < -worst);
                if (!worthKeeping) continue;

                candidates.Enqueue(neighbor, score);
                if (neighbor == exclude) continue;
                results.Enqueue(neighbor, -score);
                if (results.Count > ef) results.Dequeue();
            }
        }

        var output = new List<(int, float)>(results.Count);
        while (results.TryDequeue(out int node, out float negated)) output.Add((node, -negated));
        return output;
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
    private unsafe List<int> SelectNeighbors(float* buffer, List<(int Node, float Score)> candidates, int m)
    {
        var selected = new List<int>(m);
        List<int>? pruned = null;

        foreach (var (node, score) in candidates)
        {
            if (selected.Count >= m) break;

            bool diverse = true;
            foreach (int chosen in selected)
            {
                if (Score(buffer + (long)node * D, buffer + (long)chosen * D) < score)
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
    private unsafe void Connect(float* buffer, int node, int level, List<int> selected, int maxDegree)
    {
        lock (_graph.LockFor(node))
        {
            var slots = _graph.Neighbors(node, level);
            for (int i = 0; i < slots.Length; i++)
                slots[i] = i < selected.Count ? selected[i] : HnswGraph.NoNeighbor;
        }

        foreach (int neighbor in selected)
        {
            lock (_graph.LockFor(neighbor))
            {
                var slots = _graph.Neighbors(neighbor, level);
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
                float* neighborVector = buffer + (long)neighbor * D;
                var candidates = new List<(int, float)>(slots.Length + 1);
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != HnswGraph.NoNeighbor)
                        candidates.Add((slots[i], Score(neighborVector, buffer + (long)slots[i] * D)));
                candidates.Add((node, Score(neighborVector, buffer + (long)node * D)));
                candidates.Sort(static (a, b) => a.Item2.CompareTo(b.Item2));

                var pruned = SelectNeighbors(buffer, candidates, maxDegree);
                for (int i = 0; i < slots.Length; i++)
                    slots[i] = i < pruned.Count ? pruned[i] : HnswGraph.NoNeighbor;
            }
        }
    }

    // ------------------------------------------------------------ Searching

    public override unsafe void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (nq == 0) return;
        if (_store.Count == 0)
        {
            distances.Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels.Fill(-1);
            return;
        }

        int ef = Math.Max(EfSearch, k);
        fixed (float* xq = queries)
        fixed (float* buffer = _store.Buffer)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, bp = (nint)buffer, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            if (nq == 1 || threads == 1)
                for (int q = 0; q < nq; q++)
                    SearchOne((float*)bp, (float*)qp + (long)q * D, k, ef,
                        (float*)dp + (long)q * k, (long*)lp + (long)q * k);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    SearchOne((float*)bp, (float*)qp + (long)q * D, k, ef,
                        (float*)dp + (long)q * k, (long*)lp + (long)q * k));
        }
    }

    private unsafe void SearchOne(float* buffer, float* query, int k, int ef, float* outDistances, long* outLabels)
    {
        var scratch = RentScratch();
        try
        {
            int entry = _graph.EntryPoint;
            float entryScore = Score(query, buffer + (long)entry * D);
            for (int level = _graph.MaxLevel; level > 0; level--)
                GreedyDescend(buffer, query, ref entry, ref entryScore, level);

            var found = SearchLayer(buffer, query, entry, entryScore, ef, 0, scratch);
            found.Sort(static (a, b) => a.Score.CompareTo(b.Score));

            int count = Math.Min(k, found.Count);
            for (int i = 0; i < count; i++)
            {
                outDistances[i] = ToPublicDistance(found[i].Score);
                outLabels[i] = found[i].Node;
            }
            for (int i = count; i < k; i++)
            {
                outDistances[i] = MetricType.IsSimilarity() ? float.MinValue : float.MaxValue;
                outLabels[i] = -1;
            }
        }
        finally
        {
            _scratchPool.Add(scratch);
        }
    }

    /// <summary>
    /// Approximate radius search: explores layer 0 with a wide beam and keeps everything inside the
    /// radius. Unlike the exact range search on <see cref="IndexFlat"/> this can miss matches that
    /// the graph walk never reaches; widen <see cref="EfSearch"/> to trade time for completeness.
    /// </summary>
    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius)
    {
        int nq = ValidateBatch(queries, nameof(queries));
        if (nq == 0 || _store.Count == 0) return new RangeSearchResult(new long[nq + 1], [], []);

        var perQuery = new List<(long Id, float Distance)>[nq];
        bool similarity = MetricType.IsSimilarity();
        int ef = Math.Max(EfSearch, 64);

        fixed (float* xq = queries)
        fixed (float* buffer = _store.Buffer)
        {
            nint qp = (nint)xq, bp = (nint)buffer;
            Parallel.For(0, nq, q =>
            {
                var scratch = RentScratch();
                try
                {
                    float* bufferLocal = (float*)bp;
                    float* query = (float*)qp + (long)q * D;
                    int entry = _graph.EntryPoint;
                    float entryScore = Score(query, bufferLocal + (long)entry * D);
                    for (int level = _graph.MaxLevel; level > 0; level--)
                        GreedyDescend(bufferLocal, query, ref entry, ref entryScore, level);

                    var found = SearchLayer(bufferLocal, query, entry, entryScore, ef, 0, scratch);
                    var hits = new List<(long, float)>();
                    foreach (var (node, score) in found)
                    {
                        float distance = ToPublicDistance(score);
                        if (similarity ? distance > radius : distance < radius) hits.Add((node, distance));
                    }
                    perQuery[q] = hits;
                }
                finally
                {
                    _scratchPool.Add(scratch);
                }
            });
        }

        return BruteForce.Flatten(perQuery);
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _store.Count) throw new ArgumentOutOfRangeException(nameof(key));
        _store[(int)key].CopyTo(output);
    }

    /// <summary>
    /// Not supported: removing a node would strand the links that point at it, and repairing the
    /// graph costs as much as rebuilding it. This matches FAISS, where HNSW has no <c>remove_ids</c>.
    /// Rebuild the index, or keep a tombstone set and filter results.
    /// </summary>
    public override long RemoveIds(ReadOnlySpan<long> ids) =>
        throw new NotSupportedException(
            "IndexHNSWFlat does not support removal; rebuild the index or filter removed ids from results.");

    public override void Reset()
    {
        _store.Clear();
        _graph = new HnswGraph(M);
        Ntotal = 0;
    }

    public override long MemoryUsage => _store.MemoryUsage + _graph.MemoryUsage;

    public override string Describe() =>
        $"IndexHNSWFlat(d={D}, ntotal={Ntotal}, M={M}, efConstruction={EfConstruction}, efSearch={EfSearch}, " +
        $"{MetricType.ToShortString()}, levels={_graph.MaxLevel + 1})";

    private Scratch RentScratch() => _scratchPool.TryTake(out var scratch) ? scratch : new Scratch();

    /// <summary>Per-traversal working set, pooled so a query allocates nothing after warm-up.</summary>
    private sealed class Scratch
    {
        public readonly VisitedTable Visited = new(1024);
        public readonly PriorityQueue<int, float> Candidates = new();
        public readonly PriorityQueue<int, float> Results = new();
    }

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.HNSWFlat;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(EfConstruction);
        writer.Write(EfSearch);
        writer.Write(_store.Count);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_store.AsSpan()));
        _graph.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        EfConstruction = reader.ReadInt32();
        EfSearch = reader.ReadInt32();
        int count = reader.ReadInt32();
        var buffer = new float[(long)count * D];
        reader.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()));
        _store.Clear();
        _store.Add(buffer);
        _graph = HnswGraph.Read(reader);
        Ntotal = _store.Count;
    }
}
