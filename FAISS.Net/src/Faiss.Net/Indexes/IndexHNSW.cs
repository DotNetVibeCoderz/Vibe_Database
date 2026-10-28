using System.Collections.Concurrent;
using Faiss.Net.Core;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Base of the HNSW family: the graph, construction, search and persistence of the graph, over a
/// storage chosen by the subclass — raw floats (<see cref="IndexHNSWFlat"/>), scalar-quantized
/// codes (<see cref="IndexHNSWSQ"/>) or product-quantized codes (<see cref="IndexHNSWPQ"/>).
/// <para>
/// Search is a greedy walk: descend the sparse upper layers to land near the query, then explore
/// layer 0 with a priority queue of width <see cref="EfSearch"/>. Cost grows logarithmically with
/// the database rather than linearly.
/// </para>
/// <para>
/// <b>Concurrency.</b> Construction is multi-threaded; insertions take a per-node lock only while
/// rewriting that node's links. Searches may run while a single writer adds: a search reads the
/// entry point first and the storage snapshot second, and ignores links to nodes beyond its
/// snapshot, so it sees a consistent graph over the nodes that existed when it started. Removal is
/// not supported, as in FAISS.
/// </para>
/// </summary>
public abstract class IndexHNSW : Index
{
    private protected HnswGraph _graph;
    private readonly RandomGenerator _levelRng = new(1234);
    private readonly object _entryPointLock = new();
    private readonly ConcurrentBag<HnswScratch> _scratchPool = [];

    /// <summary>Links per node above layer 0. Higher means better recall, more memory, slower build. FAISS defaults to 32.</summary>
    public int M => _graph.M;

    /// <summary>Search width during construction. Higher builds a better graph at linear build cost.</summary>
    public int EfConstruction { get; set; } = 40;

    /// <summary>
    /// Search width at query time — the recall/latency dial, adjustable at any point after building,
    /// or per call through <see cref="SearchParametersHNSW.EfSearch"/>. Values below <c>k</c> are
    /// raised to <c>k</c>, since the queue must hold the result set.
    /// </summary>
    public int EfSearch { get; set; } = 16;

    /// <summary>
    /// Training vectors per node that <see cref="IndexIVF.Train"/> passes to
    /// <see cref="AddLevel0LinksFromSample"/> when this index is an IVF coarse quantizer. 3 by
    /// default, as in FAISS; 0 disables the step. Named <c>level_0_link_samples_per_node</c> there.
    /// </summary>
    public int Level0LinkSamplesPerNode { get; set; } = 3;

    /// <summary>The underlying graph, for inspection and diagnostics.</summary>
    public HnswGraph Graph => _graph;

    private protected IndexHNSW(int dimension, int m, MetricType metric) : base(dimension, metric)
    {
        _graph = new HnswGraph(m);
    }

    // --------------------------------------------------------- Storage hooks

    /// <summary>Encodes and appends vectors to the storage, publishing them before any link to them exists.</summary>
    private protected abstract void StorageAdd(ReadOnlySpan<float> x);

    /// <summary>Nodes in the storage.</summary>
    private protected abstract int StorageCount { get; }

    /// <summary>Pins a snapshot of the storage for one build or one batch of searches.</summary>
    private protected abstract HnswSession OpenSession();

    /// <summary>Drops every stored vector.</summary>
    private protected abstract void StorageReset();

    private protected abstract void WriteStorage(BinaryWriter writer);

    private protected abstract void ReadStorage(BinaryReader reader);

    // -------------------------------------------------------------- Adding

    public override void Add(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        int first = StorageCount;
        StorageAdd(x);

        // Levels are drawn serially so a given seed always produces the same graph shape, then all
        // link slots are reserved before any thread starts linking.
        var levels = new int[n];
        for (int i = 0; i < n; i++) levels[i] = _graph.RandomLevel(_levelRng);
        _graph.AddNodes(levels);

        using (var session = OpenSession())
        {
            if (_graph.EntryPoint < 0) _graph.SetEntryPoint(first, levels[0]);

            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;
            int start = _graph.EntryPoint == first ? 1 : 0;

            if (threads == 1 || n < 64)
            {
                var scratch = RentScratch();
                for (int i = start; i < n; i++)
                    session.Insert(_graph, first + i, levels[i], EfConstruction, scratch, _entryPointLock);
                ReturnScratch(scratch);
            }
            else
            {
                Parallel.For(start, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    var scratch = RentScratch();
                    session.Insert(_graph, first + i, levels[i], EfConstruction, scratch, _entryPointLock);
                    ReturnScratch(scratch);
                });
            }
        }

        Ntotal = StorageCount;
    }

    // ------------------------------------------------------------ Searching

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override unsafe void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        EnsureTrained();
        if (nq == 0) return;

        // Entry point first, storage second: the entry node was stored before it was linked, so a
        // snapshot taken after reading it always contains it.
        var (entry, maxLevel) = _graph.Entry;
        float worst = MetricType.IsSimilarity() ? float.MinValue : float.MaxValue;
        if (entry < 0)
        {
            distances[..(nq * k)].Fill(worst);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        int ef = Math.Max(ResolveEf(parameters), k);
        var sel = parameters?.Sel;

        using var session = OpenSession();
        fixed (float* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            void One(int q)
            {
                var scratch = RentScratch();
                try
                {
                    var found = session.Search(_graph, qp + (nint)q * D * sizeof(float), entry, maxLevel, ef, scratch, sel);
                    float* outD = (float*)dp + (long)q * k;
                    long* outL = (long*)lp + (long)q * k;
                    int count = Math.Min(k, found.Count);
                    for (int i = 0; i < count; i++)
                    {
                        outD[i] = ToPublicDistance(found[i].Score);
                        outL[i] = found[i].Node;
                    }
                    for (int i = count; i < k; i++)
                    {
                        outD[i] = worst;
                        outL[i] = -1;
                    }
                }
                finally
                {
                    ReturnScratch(scratch);
                }
            }

            if (nq == 1 || threads == 1)
                for (int q = 0; q < nq; q++) One(q);
            else
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, One);
        }
    }

    private int ResolveEf(SearchParameters? parameters) =>
        parameters is SearchParametersHNSW { EfSearch: > 0 } hnsw ? hnsw.EfSearch : EfSearch;

    /// <summary>
    /// Approximate radius search: explores layer 0 with a wide beam and keeps everything inside the
    /// radius. Unlike the exact range search on <see cref="IndexFlat"/> this can miss matches that
    /// the graph walk never reaches; widen <see cref="EfSearch"/> to trade time for completeness.
    /// </summary>
    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        RangeSearch(queries, radius, parameters: null);

    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius, SearchParameters? parameters)
    {
        EnsureTrained();
        int nq = ValidateBatch(queries, nameof(queries));
        var (entry, maxLevel) = _graph.Entry;
        if (nq == 0 || entry < 0) return new RangeSearchResult(new long[nq + 1], [], []);

        var perQuery = new List<(long Id, float Distance)>[nq];
        bool similarity = MetricType.IsSimilarity();
        int ef = Math.Max(ResolveEf(parameters), 64);
        var sel = parameters?.Sel;

        using var session = OpenSession();
        fixed (float* xq = queries)
        {
            nint qp = (nint)xq;
            Parallel.For(0, nq, q =>
            {
                var scratch = RentScratch();
                try
                {
                    var found = session.Search(_graph, qp + (nint)q * D * sizeof(float), entry, maxLevel, ef, scratch, sel);
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
                    ReturnScratch(scratch);
                }
            });
        }

        return BruteForce.Flatten(perQuery);
    }

    /// <summary>Converts the graph's smaller-is-better score back to the metric's own value.</summary>
    private protected float ToPublicDistance(float score) => MetricType == MetricType.InnerProduct ? -score : score;

    // -------------------------------------------------- Level-0 sink repair

    /// <summary>
    /// Finds <em>sinks</em> — nodes where many searches stop although a nearer node exists — and
    /// gives each one level-0 links to the true nearest nodes of the vectors it captured.
    /// The <c>add_level_0_links_from_sample</c> of FAISS (upstream #5705).
    /// <para>
    /// A sink is a property of the graph <em>and</em> of the vectors searched, which is why this
    /// needs a sample: the build only ever searches for the nodes themselves, and those searches
    /// succeed. With a small <see cref="EfSearch"/> a sink in an IVF coarse quantizer becomes an
    /// inverted list many times larger than its neighbours', and every query that probes it slows
    /// down. <see cref="IndexIVF.Train"/> calls this automatically for an HNSW quantizer.
    /// </para>
    /// <para>
    /// Only links change, and the changes are made on one thread in a fixed order, so the result does
    /// not depend on the thread count. A later <see cref="Add"/> may overwrite the new links; call this
    /// after the last add.
    /// </para>
    /// </summary>
    /// <param name="x">Sample vectors from the distribution that will be searched, <c>n * d</c>.</param>
    /// <param name="parameters">Thresholds; defaults match FAISS.</param>
    public unsafe HNSWLevel0LinkStats AddLevel0LinksFromSample(ReadOnlySpan<float> x, HNSWLevel0LinkParameters? parameters = null)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        var p = parameters ?? new HNSWLevel0LinkParameters();
        var stats = new HNSWLevel0LinkStats();
        if (n == 0 || _graph.EntryPoint < 0) return stats;

        int ef = Math.Max(1, ResolveEf(p.SearchParams));
        fixed (float* px = x)
        {
            nint xp = (nint)px;
            using var session = OpenSession();
            var linker = new HnswSinkRepair(_graph, session, xp, n, D * sizeof(float), ef, p, RentScratch, ReturnScratch,
                Threads > 0 ? Threads : Environment.ProcessorCount);
            linker.Run(stats);
        }
        return stats;
    }

    // ------------------------------------------------------------- Removal

    /// <summary>
    /// Not supported: removing a node would strand the links that point at it, and repairing the
    /// graph costs as much as rebuilding it. This matches FAISS, where HNSW has no <c>remove_ids</c>.
    /// Rebuild the index, or keep a tombstone set and exclude it with a search-time
    /// <see cref="IDSelector"/>.
    /// </summary>
    public override long RemoveIds(ReadOnlySpan<long> ids) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support removal; rebuild, or exclude removed ids with IDSelector.Not(...) at search time.");

    /// <inheritdoc cref="RemoveIds(ReadOnlySpan{long})"/>
    public override long RemoveIds(Func<long, bool> predicate) => RemoveIds(ReadOnlySpan<long>.Empty);

    public override void Reset()
    {
        StorageReset();
        _graph = new HnswGraph(M);
        Ntotal = 0;
    }

    // ------------------------------------------------------------- Helpers

    private HnswScratch RentScratch() => _scratchPool.TryTake(out var scratch) ? scratch : new HnswScratch();

    private void ReturnScratch(HnswScratch scratch) => _scratchPool.Add(scratch);

    // -------------------------------------------------------- Serialization

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(EfConstruction);
        writer.Write(EfSearch);
        WriteStorage(writer);
        _graph.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        EfConstruction = reader.ReadInt32();
        EfSearch = reader.ReadInt32();
        ReadStorage(reader);
        _graph = HnswGraph.Read(reader);
        Ntotal = StorageCount;
    }
}

/// <summary>Thresholds for <see cref="IndexHNSW.AddLevel0LinksFromSample"/>; defaults match FAISS.</summary>
public sealed class HNSWLevel0LinkParameters
{
    /// <summary>A node is a candidate sink when it captures at least this multiple of the mean sample count.</summary>
    public double MinMeanFactor { get; set; } = 3;

    /// <summary>A candidate is a sink when exact search finds a nearer node for at least this fraction of its samples.</summary>
    public double MinCloserFraction { get; set; } = 0.5;

    /// <summary>Links added to one sink per round.</summary>
    public int MaxLinksPerSink { get; set; } = 32;

    /// <summary>Detect-and-repair rounds.</summary>
    public int MaxRounds { get; set; } = 3;

    /// <summary>Relative tolerance below which a nearer node does not count, so ties and rounding do not.</summary>
    public double RelativeTolerance { get; set; } = 1e-5;

    /// <summary>Search settings for the detection searches; null uses the index's own.</summary>
    public SearchParametersHNSW? SearchParams { get; set; }
}

/// <summary>What <see cref="IndexHNSW.AddLevel0LinksFromSample"/> found and changed.</summary>
public sealed class HNSWLevel0LinkStats
{
    /// <summary>Sinks detected in the first round.</summary>
    public int SinksFound { get; internal set; }

    /// <summary>Sinks still detected after the last round.</summary>
    public int SinksRemaining { get; internal set; }

    /// <summary>Links written into free slots.</summary>
    public int LinksAdded { get; internal set; }

    /// <summary>Existing links replaced by better ones.</summary>
    public int LinksReplaced { get; internal set; }

    /// <summary>Rounds that changed the graph.</summary>
    public int Rounds { get; internal set; }

    /// <summary>Exhaustive searches run to confirm candidate sinks.</summary>
    public long ExactSearches { get; internal set; }

    public override string ToString() =>
        $"sinks {SinksFound} -> {SinksRemaining}, links +{LinksAdded} / replaced {LinksReplaced}, " +
        $"rounds {Rounds}, exact searches {ExactSearches}";
}
