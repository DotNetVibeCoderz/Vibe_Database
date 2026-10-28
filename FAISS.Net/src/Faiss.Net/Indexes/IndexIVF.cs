using System.Buffers;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Base class for inverted-file indexes, the standard way to make similarity search sublinear.
/// <para>
/// A coarse quantizer partitions the space into <c>nlist</c> Voronoi cells; every vector lives in
/// the cell of its nearest centroid. A query visits only the <c>nprobe</c> nearest cells, so it
/// examines roughly <c>nprobe / nlist</c> of the database. The classic starting point is
/// <c>nlist ~ sqrt(ntotal)</c> with <c>nprobe</c> in the single digits, giving a one to two order of
/// magnitude speedup over a flat scan.
/// </para>
/// <para>
/// The accuracy cost is real and one-sided: a true neighbour sitting just across a cell boundary is
/// invisible unless that cell is probed. <see cref="Nprobe"/> is the dial between recall and
/// latency, and it can be changed at any time after building — no retraining, no re-adding.
/// </para>
/// <para>
/// Subclasses supply only the per-list encoding and scoring; the coarse assignment, list management,
/// probing, threading and result merging live here.
/// </para>
/// </summary>
public abstract class IndexIVF : Index
{
    private Dictionary<long, (int List, int Offset)>? _directMap;
    private long _nextId;

    /// <summary>Coarse quantizer holding the <c>nlist</c> centroids. Usually an <see cref="IndexFlatL2"/>.</summary>
    public Index Quantizer { get; protected set; }

    /// <summary>Number of Voronoi cells.</summary>
    public int Nlist { get; protected set; }

    /// <summary>
    /// Cells visited per query. The single most useful tuning knob: raise it for recall, lower it
    /// for speed. Clamped to <see cref="Nlist"/>, at which point the index degenerates to an
    /// exhaustive scan with 100% recall.
    /// </summary>
    public int Nprobe { get; set; } = 1;

    /// <summary>The inverted lists.</summary>
    public InvertedLists Lists { get; protected set; }

    /// <summary>Bytes per stored code.</summary>
    public int CodeSize => Lists.CodeSize;

    /// <summary>
    /// When true, vectors are encoded relative to their cell centroid. Residuals have much smaller
    /// magnitude than the vectors themselves, so a fixed code budget resolves them far more finely —
    /// but the per-query lookup table then depends on the cell, and must be rebuilt for every probed
    /// list rather than once per query.
    /// </summary>
    public bool ByResidual { get; protected set; }

    /// <summary>Clustering settings used to train the coarse quantizer.</summary>
    public ClusteringParameters ClusteringParameters { get; set; } = new() { Iterations = 10 };

    protected IndexIVF(Index quantizer, int dimension, int nlist, int codeSize, MetricType metric)
        : base(dimension, metric)
    {
        ArgumentNullException.ThrowIfNull(quantizer);
        if (quantizer.D != dimension)
            throw new ArgumentException($"Quantizer dimension {quantizer.D} does not match index dimension {dimension}.");
        if (nlist <= 0) throw new ArgumentOutOfRangeException(nameof(nlist));

        Quantizer = quantizer;
        Nlist = nlist;
        Lists = new InvertedLists(nlist, codeSize);
        IsTrained = false;
    }

    // ------------------------------------------------------------- Training

    /// <summary>
    /// Trains the coarse quantizer with k-means, then hands the assignments to the subclass so it can
    /// train its own encoder on residuals if it uses them.
    /// </summary>
    public override void Train(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        if (Quantizer.TrainsAlone)
        {
            if (!Quantizer.IsTrained) Quantizer.Train(x);
            if (Quantizer.Ntotal != Nlist)
                throw new InvalidOperationException(
                    $"The quantizer defines {Quantizer.Ntotal} cells but this index was created with nlist={Nlist}.");
        }
        else if (Quantizer.Ntotal != Nlist)
        {
            var kmeans = new Kmeans(D, Nlist, new ClusteringParameters
            {
                Iterations = ClusteringParameters.Iterations,
                Redo = ClusteringParameters.Redo,
                MaxPointsPerCentroid = ClusteringParameters.MaxPointsPerCentroid,
                MinPointsPerCentroid = ClusteringParameters.MinPointsPerCentroid,
                Seed = ClusteringParameters.Seed,
                Verbose = ClusteringParameters.Verbose,
                Spherical = MetricType == MetricType.InnerProduct,
                Tolerance = ClusteringParameters.Tolerance,
                MiniBatchSize = ClusteringParameters.MiniBatchSize,
                MiniBatchIterations = ClusteringParameters.MiniBatchIterations,
            });
            kmeans.Train(x);
            Quantizer.Reset();
            Quantizer.Add(kmeans.Centroids);

            // An HNSW coarse quantizer can develop sinks: centroids that capture vectors belonging to
            // a neighbour, which turns into one inverted list many times larger than the rest. The
            // training vectors are exactly the distribution it will assign, so repair it with them.
            if (Quantizer is IndexHNSW hnsw && hnsw.Level0LinkSamplesPerNode > 0)
            {
                long samples = Math.Min(n, (long)hnsw.Level0LinkSamplesPerNode * hnsw.Ntotal);
                hnsw.AddLevel0LinksFromSample(x[..(int)(samples * D)]);
            }
        }

        var assignment = Assign(x, n);
        TrainEncoder(x, n, assignment);
        IsTrained = true;
    }

    /// <summary>Trains whatever encodes vectors inside a list. A no-op for <see cref="IndexIVFFlat"/>.</summary>
    protected virtual void TrainEncoder(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos) { }

    /// <summary>Nearest cell for each vector.</summary>
    public long[] Assign(ReadOnlySpan<float> x, int n)
    {
        var result = SearchResult.Allocate(n, 1);
        Quantizer.Search(x, n, 1, result.Distances, result.Labels);
        return result.Labels;
    }

    // -------------------------------------------------------------- Adding

    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        var ids = new long[n];
        for (int i = 0; i < n; i++) ids[i] = _nextId + i;
        AddWithIds(x, ids);
    }

    /// <summary>
    /// IVF indexes keep an explicit id per entry, so caller-chosen ids are supported directly —
    /// no <see cref="IndexIDMap"/> wrapper needed, exactly as in FAISS.
    /// </summary>
    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        if (ids.Length != n) throw new ArgumentException($"Expected {n} ids, got {ids.Length}.", nameof(ids));

        long[] listNos = Assign(x, n);
        var codes = new byte[(long)n * CodeSize];
        EncodeVectors(x, n, listNos, codes);

        // Group by list so each bucket grows once instead of once per vector.
        var grouped = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int list = (int)listNos[i];
            if (list < 0) list = 0;
            if (!grouped.TryGetValue(list, out var members)) grouped[list] = members = [];
            members.Add(i);
        }

        var idBuffer = new long[n];
        var codeBuffer = new byte[(long)n * CodeSize];
        foreach (var (list, members) in grouped)
        {
            for (int j = 0; j < members.Count; j++)
            {
                int i = members[j];
                idBuffer[j] = ids[i];
                codes.AsSpan(i * CodeSize, CodeSize).CopyTo(codeBuffer.AsSpan(j * CodeSize, CodeSize));
            }
            Lists.AddRange(list, idBuffer.AsSpan(0, members.Count),
                           codeBuffer.AsSpan(0, members.Count * CodeSize));
        }

        for (int i = 0; i < n; i++) _nextId = Math.Max(_nextId, ids[i] + 1);
        Ntotal = Lists.TotalSize;
        _directMap = null; // offsets shifted
    }

    /// <summary>Encodes <paramref name="n"/> vectors, each already assigned to a list.</summary>
    protected abstract void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes);

    // ------------------------------------------------------------ Searching

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override unsafe void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        EnsureTrained();
        if (nq == 0) return;
        if (Ntotal == 0)
        {
            distances[..(nq * k)].Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        var ivfParams = parameters as SearchParametersIVF;
        int nprobe = Math.Clamp(ivfParams is { Nprobe: > 0 } ? ivfParams.Nprobe : Nprobe, 1, Nlist);
        var scan = new ScanOptions(parameters?.Sel, ivfParams?.MaxCodes ?? 0);

        var coarse = SearchResult.Allocate(nq, nprobe);
        Quantizer.Search(queries, nq, nprobe, coarse.Distances, coarse.Labels, ivfParams?.QuantizerParams);

        fixed (float* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            if (nq == 1 || threads == 1)
            {
                for (int q = 0; q < nq; q++)
                    SearchOne(new ReadOnlySpan<float>((float*)qp + (long)q * D, D),
                        coarse.Distances.AsSpan(q * nprobe, nprobe), coarse.Labels.AsSpan(q * nprobe, nprobe),
                        k, (float*)dp + (long)q * k, (long*)lp + (long)q * k, scan);
            }
            else
            {
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
                    SearchOne(new ReadOnlySpan<float>((float*)qp + (long)q * D, D),
                        coarse.Distances.AsSpan(q * nprobe, nprobe), coarse.Labels.AsSpan(q * nprobe, nprobe),
                        k, (float*)dp + (long)q * k, (long*)lp + (long)q * k, scan));
            }
        }
    }

    /// <summary>Per-call scan settings, resolved once per search.</summary>
    private readonly record struct ScanOptions(IDSelector? Selector, long MaxCodes);

    private unsafe void SearchOne(
        ReadOnlySpan<float> query, ReadOnlySpan<float> coarseScores, ReadOnlySpan<long> coarseLists,
        int k, float* outDistances, long* outLabels, ScanOptions scan)
    {
        if (MetricType.IsSimilarity())
            SearchOneOrdered<DescendingOrder>(query, coarseScores, coarseLists, k, outDistances, outLabels, scan);
        else
            SearchOneOrdered<AscendingOrder>(query, coarseScores, coarseLists, k, outDistances, outLabels, scan);
    }

    private unsafe void SearchOneOrdered<TOrder>(
        ReadOnlySpan<float> query, ReadOnlySpan<float> coarseScores, ReadOnlySpan<long> coarseLists,
        int k, float* outDistances, long* outLabels, ScanOptions scan) where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        var sel = scan.Selector;

        float[] scores = ArrayPool<float>.Shared.Rent(256);
        int[] members = sel is null ? [] : ArrayPool<int>.Shared.Rent(256);
        object? state = PrepareQuery(query);
        long scanned = 0;
        try
        {
            for (int p = 0; p < coarseLists.Length; p++)
            {
                int list = (int)coarseLists[p];
                if (list < 0) continue;

                // One read of the list: ids and codes stay consistent even while a writer appends.
                var view = Lists.GetList(list);
                int size = view.Count;
                if (size == 0) continue;
                // A list whose id range cannot hold a member is skipped without being scored.
                if (sel is not null && !sel.MayIntersect(view.MinId, view.MaxId)) continue;

                if (size > scores.Length)
                {
                    ArrayPool<float>.Shared.Return(scores);
                    scores = ArrayPool<float>.Shared.Rent(size);
                }

                var ids = view.Ids;
                if (sel is null)
                {
                    var slice = scores.AsSpan(0, size);
                    ComputeListScores(query, list, coarseScores[p], view.Codes, slice, state);
                    for (int i = 0; i < size; i++)
                    {
                        float score = slice[i];
                        if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, ids[i]);
                    }
                }
                else
                {
                    // Membership first, as FAISS does: a selective filter then also saves the
                    // distance computations of the entries it rejects, not just their heap pushes.
                    if (members.Length < size)
                    {
                        ArrayPool<int>.Shared.Return(members);
                        members = ArrayPool<int>.Shared.Rent(size);
                    }
                    int count = 0;
                    for (int i = 0; i < size; i++)
                        if (sel.IsMember(ids[i])) members[count++] = i;
                    if (count > 0)
                    {
                        var offsets = members.AsSpan(0, count);
                        var slice = scores.AsSpan(0, count);
                        if (count == size)
                            ComputeListScores(query, list, coarseScores[p], view.Codes, slice, state);
                        else
                            ComputeListScoresSubset(query, list, coarseScores[p], view.Codes, offsets, slice, state);
                        for (int i = 0; i < count; i++)
                        {
                            float score = slice[i];
                            if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, ids[offsets[i]]);
                        }
                    }
                }

                scanned += size;
                if (scan.MaxCodes > 0 && scanned >= scan.MaxCodes) break;
            }
        }
        finally
        {
            ReleaseQuery(state);
            ArrayPool<float>.Shared.Return(scores);
            if (members.Length > 0) ArrayPool<int>.Shared.Return(members);
        }

        heap.Finish();
    }

    /// <summary>
    /// Scores every entry of one list against the query, writing <c>codes.Length / CodeSize</c> values.
    /// <para>
    /// Scoring a whole list at a time — rather than exposing a per-candidate distance callback — is
    /// what keeps the inner loop tight: the subclass can hoist all per-list setup (residual, lookup
    /// table) out of the loop, and the result heap never appears inside it.
    /// </para>
    /// </summary>
    /// <param name="coarseScore">Query-to-centroid score for this list, reusable by the subclass.</param>
    /// <param name="codes">The list's codes, from the same snapshot as the ids the caller reads.</param>
    /// <param name="queryState">Whatever <see cref="PrepareQuery"/> returned for this query.</param>
    protected abstract void ComputeListScores(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes, Span<float> scores,
        object? queryState);

    /// <summary>
    /// Scores only the entries at <paramref name="offsets"/> (ascending positions within the list),
    /// writing one score per offset. Used by filtered search, so rejected entries cost no distance.
    /// The default scores the whole list and gathers, which is correct for any subclass; the
    /// built-in subclasses override it to skip the rejected entries' work.
    /// </summary>
    protected virtual void ComputeListScoresSubset(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes,
        ReadOnlySpan<int> offsets, Span<float> scores, object? queryState)
    {
        int size = codes.Length / CodeSize;
        float[] full = ArrayPool<float>.Shared.Rent(size);
        try
        {
            ComputeListScores(query, list, coarseScore, codes, full.AsSpan(0, size), queryState);
            for (int i = 0; i < offsets.Length; i++) scores[i] = full[offsets[i]];
        }
        finally
        {
            ArrayPool<float>.Shared.Return(full);
        }
    }

    /// <summary>
    /// Per-query setup shared by every probed list — a lookup-table term that does not depend on the
    /// cell, for instance. Called once per query before any list is scored. The default does nothing.
    /// </summary>
    protected virtual object? PrepareQuery(ReadOnlySpan<float> query) => null;

    /// <summary>Releases what <see cref="PrepareQuery"/> allocated.</summary>
    protected virtual void ReleaseQuery(object? queryState) { }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        RangeSearch(queries, radius, parameters: null);

    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius, SearchParameters? parameters)
    {
        EnsureTrained();
        int nq = ValidateBatch(queries, nameof(queries));
        if (nq == 0) return new RangeSearchResult(new long[1], [], []);

        var ivfParams = parameters as SearchParametersIVF;
        int nprobe = Math.Clamp(ivfParams is { Nprobe: > 0 } ? ivfParams.Nprobe : Nprobe, 1, Nlist);
        var sel = parameters?.Sel;
        var coarse = SearchResult.Allocate(nq, nprobe);
        Quantizer.Search(queries, nq, nprobe, coarse.Distances, coarse.Labels, ivfParams?.QuantizerParams);

        var perQuery = new List<(long Id, float Distance)>[nq];
        bool similarity = MetricType.IsSimilarity();

        fixed (float* xq = queries)
        {
            nint qp = (nint)xq;
            Parallel.For(0, nq, q =>
            {
                var hits = new List<(long, float)>();
                var query = new ReadOnlySpan<float>((float*)qp + (long)q * D, D);
                float[] scores = [];
                object? state = PrepareQuery(query);
                try
                {
                    for (int p = 0; p < nprobe; p++)
                    {
                        int list = (int)coarse.Labels[q * nprobe + p];
                        if (list < 0) continue;
                        var view = Lists.GetList(list);
                        int size = view.Count;
                        if (size == 0) continue;
                        if (sel is not null && !sel.MayIntersect(view.MinId, view.MaxId)) continue;
                        if (size > scores.Length) scores = new float[size];

                        var slice = scores.AsSpan(0, size);
                        ComputeListScores(query, list, coarse.Distances[q * nprobe + p], view.Codes, slice, state);
                        var ids = view.Ids;
                        for (int i = 0; i < size; i++)
                            if ((similarity ? slice[i] > radius : slice[i] < radius) && (sel is null || sel.IsMember(ids[i])))
                                hits.Add((ids[i], slice[i]));
                    }
                }
                finally
                {
                    ReleaseQuery(state);
                }
                perQuery[q] = hits;
            });
        }

        return Core.BruteForce.Flatten(perQuery);
    }

    // ------------------------------------------------------------- Removal

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        long removed = Lists.RemoveIds(predicate);
        Ntotal = Lists.TotalSize;
        _directMap = null;
        return removed;
    }

    public override void Reset()
    {
        Lists.Reset();
        Ntotal = 0;
        _nextId = 0;
        _directMap = null;
    }

    // ------------------------------------------------------- Reconstruction

    /// <summary>
    /// Builds the id-to-location table that <see cref="Reconstruct(long, Span{float})"/> needs.
    /// It is not maintained automatically because it would double the memory cost of ids for the
    /// many workloads that never reconstruct — same rationale as <c>make_direct_map()</c> in FAISS.
    /// </summary>
    public void MakeDirectMap()
    {
        var map = new Dictionary<long, (int, int)>((int)Math.Min(int.MaxValue, Ntotal));
        for (int list = 0; list < Nlist; list++)
        {
            var ids = Lists.GetIds(list);
            for (int offset = 0; offset < ids.Length; offset++) map[ids[offset]] = (list, offset);
        }
        _directMap = map;
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (_directMap is null)
            throw new InvalidOperationException("Call MakeDirectMap() before reconstructing from an IVF index.");
        if (!_directMap.TryGetValue(key, out var location))
            throw new ArgumentOutOfRangeException(nameof(key), $"Id {key} is not in the index.");
        DecodeEntry(location.List, location.Offset, output);
    }

    /// <summary>Decodes one stored entry back to a vector.</summary>
    protected abstract void DecodeEntry(int list, int offset, Span<float> output);

    /// <summary>The centroid of one cell.</summary>
    protected void GetCentroid(int list, Span<float> output) => Quantizer.Reconstruct(list, output);

    // -------------------------------------------------------------- Utility

    public override long MemoryUsage => Lists.MemoryUsage + Quantizer.MemoryUsage;

    /// <summary>List balance statistics; see <see cref="InvertedLists.Statistics"/>.</summary>
    public (int Min, int Max, double Mean, int Empty) ListStatistics() => Lists.Statistics();

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {MetricType.ToShortString()})";

    /// <summary>Restores state during deserialization.</summary>
    protected internal void RestoreIvf(Index quantizer, InvertedLists lists, int nlist)
    {
        Quantizer = quantizer;
        Lists = lists;
        Nlist = nlist;
        Ntotal = lists.TotalSize;
        _nextId = 0;
        for (int list = 0; list < nlist; list++)
            foreach (long id in lists.GetIds(list)) _nextId = Math.Max(_nextId, id + 1);
    }
}
