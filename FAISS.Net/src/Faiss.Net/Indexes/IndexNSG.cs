using System.Runtime.InteropServices;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Navigating Spreading-out Graph (Fu et al., VLDB 2019) over full-precision vectors — the FAISS
/// <c>IndexNSGFlat</c>.
/// <para>
/// Where HNSW keeps a hierarchy of layers, NSG is a single flat graph with one fixed entry point
/// (the navigating node, near the medoid), so a search is one beam walk with no descent. The graph
/// is built in one pass from a k-nearest-neighbour graph: each node keeps up to <see cref="R"/>
/// links chosen by the MRNG rule — a candidate is kept only if no kept neighbour is closer to it
/// than the node is, the same diversity test HNSW uses — reverse links are added and re-pruned, and
/// a final depth-first sweep attaches any node the entry point cannot reach, so every node is
/// guaranteed reachable.
/// </para>
/// <para>
/// The trade against HNSW: a smaller graph (one layer, out-degree at most R) and cheaper search per
/// hop, but the graph is built from the whole dataset at once — like FAISS, <see cref="Add"/> may be
/// called only once; rebuild to change the data. The k-NN graph is computed exactly (blocked matrix
/// product) up to <see cref="ExactKnnLimit"/> vectors and with an HNSW above it.
/// </para>
/// <para>
/// <b>Weak spot: well-separated clusters.</b> Because the graph is derived from a k-NN graph, data
/// made of tight, far-apart clusters yields a k-NN graph with no edges between clusters; the
/// connectivity pass then bridges each cluster with a single edge, and a beam search often fails to
/// cross it. Measured on 32 clusters at spread 0.1: recall@10 42% where HNSW reaches 98%, against
/// 99.9% (HNSW 99.2%) once the clusters overlap at spread 0.3 and 99.7% on uniform data. Prefer
/// <see cref="IndexHNSWFlat"/> when the data is strongly clustered.
/// </para>
/// </summary>
public sealed class IndexNSGFlat : Index
{
    private float[] _vectors = [];
    private int _count;
    private int[] _graph = [];
    private int _entry = -1;

    /// <summary>Maximum out-degree.</summary>
    public int R { get; private set; }

    /// <summary>Search pool size during construction. FAISS default <c>R + 32</c>.</summary>
    public int L { get; set; }

    /// <summary>Candidate pool size considered by pruning. FAISS default <c>R + 100</c>.</summary>
    public int C { get; set; }

    /// <summary>Neighbours per node in the initial k-NN graph. FAISS default 64.</summary>
    public int GK { get; set; } = 64;

    /// <summary>Beam width at query time; overridable per call through <see cref="SearchParametersHNSW.EfSearch"/>.</summary>
    public int SearchL { get; set; } = 16;

    /// <summary>Largest dataset whose k-NN graph is computed exactly; above it an HNSW approximates it.</summary>
    public int ExactKnnLimit { get; set; } = 200_000;

    /// <summary>The navigating node every search starts from; -1 before building.</summary>
    public int EnterPoint => _entry;

    /// <summary>True once the graph has been built.</summary>
    public bool IsBuilt => _entry >= 0;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="r">Maximum out-degree; FAISS default 32.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexNSGFlat(int dimension, int r = 32, MetricType metric = MetricType.L2) : base(dimension, metric)
    {
        if (r < 2) throw new ArgumentOutOfRangeException(nameof(r), "R must be at least 2.");
        R = r;
        L = r + 32;
        C = r + 100;
        IsTrained = true;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Stored vectors, <c>ntotal * d</c> row-major.</summary>
    public ReadOnlySpan<float> Vectors => _vectors.AsSpan(0, _count * D);

    /// <summary>Links of one node; unused slots are -1.</summary>
    public ReadOnlySpan<int> Neighbors(int node) => _graph.AsSpan(node * R, R);

    /// <summary>Mean out-degree, a quick health check of a built graph.</summary>
    public double AverageDegree()
    {
        if (_count == 0) return 0;
        long links = 0;
        foreach (int link in _graph.AsSpan(0, _count * R)) if (link >= 0) links++;
        return links / (double)_count;
    }

    private float Score(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        MetricType == MetricType.InnerProduct ? -VectorOps.InnerProduct(a, b) : VectorOps.Distance(a, b, MetricType);

    private ReadOnlySpan<float> Row(int node) => _vectors.AsSpan(node * D, D);

    // ----------------------------------------------------------- Building

    /// <summary>Stores the vectors and builds the graph. NSG is built once from all data, as in FAISS.</summary>
    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        if (IsBuilt)
            throw new InvalidOperationException(
                "IndexNSGFlat is built once from the whole dataset and does not support incremental adds. " +
                "Call Reset() and add everything again, or use IndexHNSWFlat for incremental workloads.");

        _vectors = x[..(n * D)].ToArray();
        _count = n;
        Build(BuildKnnGraph(n));
        Ntotal = n;
    }

    /// <summary>k nearest neighbours of every node, excluding itself, as an <c>n x gk</c> table.</summary>
    private int[] BuildKnnGraph(int n)
    {
        int gk = Math.Min(GK, n - 1);
        var knn = new int[(long)n * Math.Max(gk, 1)];
        if (gk <= 0) return knn;

        SearchResult result;
        if (n <= ExactKnnLimit)
        {
            var flat = new IndexFlat(D, MetricType);
            flat.Add(_vectors);
            result = flat.Search(_vectors, gk + 1);
        }
        else
        {
            var hnsw = new IndexHNSWFlat(D, 32, MetricType) { EfConstruction = 40, EfSearch = 2 * gk };
            hnsw.Add(_vectors);
            result = hnsw.Search(_vectors, gk + 1);
        }

        for (int i = 0; i < n; i++)
        {
            int written = 0;
            foreach (long id in result.LabelsFor(i))
            {
                if (id == i || id < 0) continue;
                if (written == gk) break;
                knn[(long)i * gk + written++] = (int)id;
            }
            for (; written < gk; written++) knn[(long)i * gk + written] = (int)result.LabelsFor(i)[0];
        }
        GKBuilt = gk;
        return knn;
    }

    private int GKBuilt { get; set; }

    private void Build(int[] knn)
    {
        int n = _count, gk = GKBuilt;
        _graph = new int[(long)n * R];
        Array.Fill(_graph, -1);

        // Navigating node: search the k-NN graph for the vector nearest the centroid.
        var centroid = new float[D];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < D; j++) centroid[j] += _vectors[i * D + j];
        for (int j = 0; j < D; j++) centroid[j] /= n;
        _entry = 0;
        var visited = new VisitedTable(n);
        var pool = SearchGraph(knn, gk, centroid, 0, L, visited, collect: null);
        _entry = pool.Count > 0 ? pool[0].Node : 0;

        // Link: every node prunes the pool of a search for itself plus its own k-NN.
        var degrees = new int[n];
        var locks = new object[n];
        for (int i = 0; i < n; i++) locks[i] = new object();

        Parallel.For(0, n, () => new VisitedTable(n), (i, _, local) =>
        {
            var fullset = new Dictionary<int, float>();
            SearchGraph(knn, gk, Row(i), _entry, L, local, fullset);
            for (int j = 0; j < gk; j++)
            {
                int neighbor = knn[(long)i * gk + j];
                if (!fullset.ContainsKey(neighbor)) fullset[neighbor] = Score(Row(i), Row(neighbor));
            }
            fullset.Remove(i);

            var candidates = fullset.Select(kv => (Node: kv.Key, Score: kv.Value)).ToList();
            candidates.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Node.CompareTo(b.Node));
            if (candidates.Count > C) candidates.RemoveRange(C, candidates.Count - C);

            var kept = Prune(i, candidates);
            lock (locks[i])
            {
                for (int s = 0; s < kept.Count; s++) _graph[(long)i * R + s] = kept[s];
                degrees[i] = kept.Count;
            }
            return local;
        }, _ => { });

        // Reverse links, re-pruned when a node is already full. Collected first, applied in node
        // order, so the graph does not depend on thread scheduling.
        var reverse = new List<int>[n];
        for (int i = 0; i < n; i++)
            for (int s = 0; s < R; s++)
            {
                int j = _graph[(long)i * R + s];
                if (j < 0) break;
                (reverse[j] ??= []).Add(i);
            }

        Parallel.For(0, n, j =>
        {
            var incoming = reverse[j];
            if (incoming is null) return;
            var current = new HashSet<int>();
            for (int s = 0; s < R; s++)
            {
                int link = _graph[(long)j * R + s];
                if (link < 0) break;
                current.Add(link);
            }
            var additions = incoming.Where(i => !current.Contains(i)).ToList();
            if (additions.Count == 0) return;

            if (current.Count + additions.Count <= R)
            {
                int slot = current.Count;
                foreach (int i in additions) _graph[(long)j * R + slot++] = i;
                degrees[j] = slot;
                return;
            }

            var candidates = current.Concat(additions).Distinct()
                .Select(node => (Node: node, Score: Score(Row(j), Row(node))))
                .ToList();
            candidates.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Node.CompareTo(b.Node));
            var kept = Prune(j, candidates);
            for (int s = 0; s < R; s++) _graph[(long)j * R + s] = s < kept.Count ? kept[s] : -1;
            degrees[j] = kept.Count;
        });

        GrowTree(degrees);
    }

    /// <summary>
    /// MRNG pruning: walk candidates nearest first and keep one only if no kept neighbour is closer
    /// to it than the node itself. Rejected candidates back-fill up to <see cref="R"/>, as HNSW's
    /// <c>keepPrunedConnections</c> does, because in high dimension the rule alone leaves nodes far
    /// below R links.
    /// </summary>
    private List<int> Prune(int node, List<(int Node, float Score)> candidates)
    {
        var kept = new List<int>(R);
        var rejected = new List<int>();
        foreach (var (candidate, score) in candidates)
        {
            if (kept.Count >= R) break;
            bool occluded = false;
            foreach (int chosen in kept)
            {
                if (Score(Row(candidate), Row(chosen)) < score)
                {
                    occluded = true;
                    break;
                }
            }
            if (occluded) rejected.Add(candidate);
            else kept.Add(candidate);
        }
        for (int i = 0; i < rejected.Count && kept.Count < R; i++) kept.Add(rejected[i]);
        return kept;
    }

    /// <summary>
    /// Depth-first sweep from the navigating node. Every node it cannot reach is attached to the
    /// nearest reachable node that still has a free slot, then the sweep continues from it — so after
    /// this, every vector is reachable from the entry point and none is silently unsearchable.
    /// </summary>
    private void GrowTree(int[] degrees)
    {
        int n = _count;
        var reached = new bool[n];
        int reachedCount = 0;
        var visited = new VisitedTable(n);

        void Sweep(int root)
        {
            var stack = new Stack<int>();
            stack.Push(root);
            if (!reached[root]) { reached[root] = true; reachedCount++; }
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                for (int s = 0; s < R; s++)
                {
                    int link = _graph[(long)node * R + s];
                    if (link < 0) break;
                    if (reached[link]) continue;
                    reached[link] = true;
                    reachedCount++;
                    stack.Push(link);
                }
            }
        }

        // Overwriting a full parent's last link (below) can, rarely, cut off a node an earlier sweep
        // had reached, so reachability is recomputed from scratch until a pass attaches nothing.
        for (int pass = 0; pass < 8; pass++)
        {
            Array.Clear(reached);
            reachedCount = 0;
            Sweep(_entry);
            if (reachedCount == n) return;
            AttachStranded();
        }

        void AttachStranded()
        {
        for (int node = 0; node < n && reachedCount < n; node++)
        {
            if (reached[node]) continue;

            // Nearest reachable node with room, from a search for the stranded vector.
            var pool = SearchGraph(_graph, R, Row(node), _entry, C, visited, collect: null);
            int parent = -1;
            foreach (var (candidate, _) in pool)
                if (reached[candidate] && degrees[candidate] < R) { parent = candidate; break; }
            if (parent < 0)
                for (int candidate = 0; candidate < n; candidate++)
                    if (reached[candidate] && degrees[candidate] < R) { parent = candidate; break; }
            if (parent < 0)
            {
                // Every reachable node is full: replace the parent's last link. That link's target is
                // reachable through the parent's other links or will be re-attached by this loop.
                parent = pool.Count > 0 && reached[pool[0].Node] ? pool[0].Node : _entry;
                degrees[parent] = R - 1;
            }

            _graph[(long)parent * R + degrees[parent]] = node;
            degrees[parent]++;
            Sweep(node);
        }
        }
    }

    /// <summary>
    /// Beam search over a fixed-degree graph from one entry point, keeping the <paramref name="pool"/>
    /// best. When <paramref name="collect"/> is given, every scored node is recorded in it — the
    /// "full set" NSG prunes from.
    /// </summary>
    private List<(int Node, float Score)> SearchGraph(
        int[] graph, int degree, ReadOnlySpan<float> query, int entry, int pool, VisitedTable visited,
        Dictionary<int, float>? collect, IDSelector? sel = null)
    {
        var candidates = new PriorityQueue<int, float>();
        var results = new PriorityQueue<int, float>();
        visited.Reset(_count);

        float entryScore = Score(query, Row(entry));
        visited.Visit(entry);
        candidates.Enqueue(entry, entryScore);
        if (sel is null || sel.IsMember(entry)) results.Enqueue(entry, -entryScore);
        collect?.TryAdd(entry, entryScore);

        while (candidates.TryPeek(out int current, out float currentScore))
        {
            if (results.Count >= pool && results.TryPeek(out _, out float worstNegated) && currentScore > -worstNegated)
                break;
            candidates.Dequeue();

            for (int s = 0; s < degree; s++)
            {
                int neighbor = graph[(long)current * degree + s];
                if (neighbor < 0) break;
                if (!visited.Visit(neighbor)) continue;

                float score = Score(query, Row(neighbor));
                collect?.TryAdd(neighbor, score);
                bool worthKeeping = results.Count < pool || (results.TryPeek(out _, out float worst) && score < -worst);
                if (!worthKeeping) continue;

                candidates.Enqueue(neighbor, score);
                if (sel is not null && !sel.IsMember(neighbor)) continue;
                results.Enqueue(neighbor, -score);
                if (results.Count > pool) results.Dequeue();
            }
        }

        var output = new List<(int, float)>(results.Count);
        while (results.TryDequeue(out int node, out float negated)) output.Add((node, -negated));
        output.Reverse();
        return output;
    }

    // ------------------------------------------------------------ Searching

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override unsafe void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        if (nq == 0) return;
        float worst = MetricType.IsSimilarity() ? float.MinValue : float.MaxValue;
        if (!IsBuilt)
        {
            distances[..(nq * k)].Fill(worst);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        int pool = Math.Max(parameters is SearchParametersHNSW { EfSearch: > 0 } p ? p.EfSearch : SearchL, k);
        var sel = parameters?.Sel;
        var queryArray = queries[..(nq * D)].ToArray();
        var outD = new float[(long)nq * k];
        var outL = new long[(long)nq * k];
        int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

        Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = nq == 1 ? 1 : threads },
            () => new VisitedTable(_count), (q, _, visited) =>
            {
                var found = SearchGraph(_graph, R, queryArray.AsSpan(q * D, D), _entry, pool, visited, null, sel);
                for (int i = 0; i < k; i++)
                {
                    bool has = i < found.Count;
                    outD[(long)q * k + i] = has ? (MetricType == MetricType.InnerProduct ? -found[i].Score : found[i].Score) : worst;
                    outL[(long)q * k + i] = has ? found[i].Node : -1;
                }
                return visited;
            }, _ => { });

        outD.CopyTo(distances);
        outL.CopyTo(labels);
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _count) throw new ArgumentOutOfRangeException(nameof(key));
        Row((int)key).CopyTo(output);
    }

    public override void ComputeDistanceSubset(ReadOnlySpan<float> query, ReadOnlySpan<long> ids, Span<float> distances)
    {
        float worst = MetricType.IsSimilarity() ? float.MinValue : float.MaxValue;
        for (int i = 0; i < ids.Length; i++)
            distances[i] = ids[i] >= 0 && ids[i] < _count ? VectorOps.Distance(query, Row((int)ids[i]), MetricType) : worst;
    }

    /// <summary>Not supported; removing a node would strand the links that lead through it. Rebuild instead.</summary>
    public override long RemoveIds(ReadOnlySpan<long> ids) =>
        throw new NotSupportedException("IndexNSGFlat does not support removal; rebuild, or exclude ids with an IDSelector.");

    /// <inheritdoc cref="RemoveIds(ReadOnlySpan{long})"/>
    public override long RemoveIds(Func<long, bool> predicate) => RemoveIds(ReadOnlySpan<long>.Empty);

    public override void Reset()
    {
        _vectors = [];
        _graph = [];
        _count = 0;
        _entry = -1;
        Ntotal = 0;
    }

    public override long MemoryUsage => (long)_vectors.Length * sizeof(float) + (long)_graph.Length * sizeof(int);

    public override string Describe() =>
        $"IndexNSGFlat(d={D}, ntotal={Ntotal}, R={R}, searchL={SearchL}, {MetricType.ToShortString()})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.NSGFlat;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(R);
        writer.Write(L);
        writer.Write(C);
        writer.Write(GK);
        writer.Write(SearchL);
        writer.Write(_entry);
        writer.Write(_count);
        writer.Write(MemoryMarshal.AsBytes(_vectors.AsSpan(0, _count * D)));
        writer.Write(MemoryMarshal.AsBytes(_graph.AsSpan(0, _count * R)));
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        R = reader.ReadInt32();
        L = reader.ReadInt32();
        C = reader.ReadInt32();
        GK = reader.ReadInt32();
        SearchL = reader.ReadInt32();
        _entry = reader.ReadInt32();
        _count = reader.ReadInt32();
        _vectors = new float[(long)_count * D];
        reader.ReadExactly(MemoryMarshal.AsBytes(_vectors.AsSpan()));
        _graph = new int[(long)_count * R];
        reader.ReadExactly(MemoryMarshal.AsBytes(_graph.AsSpan()));
        Ntotal = _count;
    }
}
