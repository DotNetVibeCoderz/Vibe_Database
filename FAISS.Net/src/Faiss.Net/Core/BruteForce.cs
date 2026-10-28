using System.Buffers;
using Faiss.Net.Utils;

namespace Faiss.Net.Core;

/// <summary>
/// Exhaustive (brute-force) search kernels shared by <c>IndexFlat</c>, the coarse quantizer inside
/// every IVF index, k-means assignment and the HNSW entry-point scan.
/// <para>
/// Two parallel strategies are chosen automatically, because the two regimes have opposite shapes:
/// batch search (many queries) parallelizes over queries, giving each thread a private heap and
/// perfect cache behaviour; a single query against a large database instead parallelizes over
/// database blocks and merges the per-block heaps. Without the second path an interactive one-query
/// lookup would run on a single core, which is the common case in an application.
/// </para>
/// <para>All scratch memory comes from <see cref="ArrayPool{T}"/>; a search allocates nothing on the heap.</para>
/// </summary>
public static unsafe class BruteForce
{
    /// <summary>Below this many candidate distances, threading costs more than it saves.</summary>
    private const long ParallelThreshold = 100_000;

    /// <summary>
    /// Routes large L2 / inner-product batches through the blocked matrix-product kernel
    /// (<see cref="BlockedKnn"/>). On by default; turn off to force the pairwise kernels, e.g. to
    /// compare the two or to get distances computed by direct subtraction.
    /// </summary>
    public static bool UseBlockedKernel { get; set; } = true;

    /// <summary>
    /// k-nearest neighbours of every query against a contiguous database.
    /// </summary>
    /// <param name="xq">Query vectors, <c>nq * d</c> row-major.</param>
    /// <param name="xb">Database vectors, <c>nb * d</c> row-major.</param>
    /// <param name="ids">Optional id mapping; when null, result ids are database row indices.</param>
    /// <param name="outDistances">Output <c>nq * k</c> distances, best first.</param>
    /// <param name="outLabels">Output <c>nq * k</c> ids, <c>-1</c> padded.</param>
    /// <param name="sel">
    /// Optional filter over result ids (after <paramref name="ids"/> mapping). Rejected entries are
    /// skipped before their distance is computed, so a selective filter also makes the scan cheaper.
    /// </param>
    public static void Knn(
        float* xq, int nq, float* xb, int nb, int d, int k,
        MetricType metric, float* outDistances, long* outLabels,
        long* ids = null, int maxThreads = 0, IDSelector? sel = null)
    {
        if (sel is not null)
        {
            FilteredKnn(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads, sel);
            return;
        }
        if (metric.IsSimilarity())
            KnnCore<DescendingOrder>(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads, null);
        else
            KnnCore<AscendingOrder>(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads, null);
    }

    /// <summary>Gathered subsets above this many bytes are scanned in place behind a mask instead.</summary>
    private const long MaxGatherBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Filtered exhaustive search. The selector is evaluated <em>once per database row</em>, not once
    /// per (query, row) — with 1,000 queries that is a thousandth of the selector calls. When the
    /// subset is at most half the database, its rows are gathered into one contiguous buffer and
    /// searched by the ordinary unfiltered kernels, so a filtered batch gets the blocked matrix
    /// product too; otherwise rows are skipped in place by a one-byte mask test.
    /// </summary>
    private static void FilteredKnn(
        float* xq, int nq, float* xb, int nb, int d, int k, MetricType metric,
        float* outDistances, long* outLabels, long* ids, int maxThreads, IDSelector sel)
    {
        int threads = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        var mask = new byte[nb];
        nint pi = (nint)ids;
        int chunks = Math.Max(1, Math.Min(threads, nb / 4096));
        int chunkSize = (nb + chunks - 1) / chunks;
        var memberCounts = new int[chunks];
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
        {
            long* rowIds = (long*)pi;
            int end = Math.Min(nb, (c + 1) * chunkSize), count = 0;
            for (int j = c * chunkSize; j < end; j++)
                if (sel.IsMember(rowIds != null ? rowIds[j] : j)) { mask[j] = 1; count++; }
            memberCounts[c] = count;
        });
        int members = memberCounts.Sum();

        bool similarity = metric.IsSimilarity();
        if (members == 0)
        {
            new Span<float>(outDistances, nq * k).Fill(similarity ? float.MinValue : float.MaxValue);
            new Span<long>(outLabels, nq * k).Fill(-1);
            return;
        }

        if (members == nb)
        {
            Knn(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads);
            return;
        }

        if (members <= nb / 2 && (long)members * d * sizeof(float) <= MaxGatherBytes)
        {
            var gathered = GC.AllocateUninitializedArray<float>(members * d);
            var gatheredIds = new long[members];
            int write = 0;
            for (int j = 0; j < nb; j++)
            {
                if (mask[j] == 0) continue;
                new ReadOnlySpan<float>(xb + (long)j * d, d).CopyTo(gathered.AsSpan(write * d, d));
                gatheredIds[write++] = ids != null ? ids[j] : j;
            }
            fixed (float* pg = gathered)
            fixed (long* pgi = gatheredIds)
                Knn(xq, nq, pg, members, d, k, metric, outDistances, outLabels, pgi, maxThreads);
            return;
        }

        // Large subset: scan in place, skipping rejected rows with a byte test. The mask is indexed by
        // row, so it stands in for the selector only when rows are the ids.
        var rowSelector = ids == null ? new RowMask(mask) : sel;
        if (similarity)
            KnnCore<DescendingOrder>(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads, rowSelector);
        else
            KnnCore<AscendingOrder>(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, maxThreads, rowSelector);
    }

    /// <summary>A precomputed membership mask over row positions.</summary>
    private sealed class RowMask(byte[] mask) : IDSelector
    {
        public override bool IsMember(long id) => mask[id] != 0;
    }

    private static void KnnCore<TOrder>(
        float* xq, int nq, float* xb, int nb, int d, int k,
        MetricType metric, float* outDistances, long* outLabels,
        long* ids, int maxThreads, IDSelector? sel) where TOrder : struct, IScoreOrder
    {
        int threads = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        long work = (long)nq * nb;

        // Batches large enough to amortize norms and transposes go through the blocked matrix
        // product; a selector keeps the pairwise path, which skips rejected rows before scoring them.
        if (sel is null && UseBlockedKernel && BlockedKnn.Applies(nq, nb, d, metric))
        {
            BlockedKnn.Knn<TOrder>(xq, nq, xb, nb, d, k, metric, outDistances, outLabels, ids, threads);
            return;
        }

        if (threads == 1 || work < ParallelThreshold)
        {
            for (int q = 0; q < nq; q++)
                ScanOne<TOrder>(xq + (long)q * d, xb, nb, d, k, metric,
                    outDistances + (long)q * k, outLabels + (long)q * k, ids, 0, sel);
            return;
        }

        if (nq >= threads)
        {
            // Enough queries to keep every core busy: one query per work item, private heaps.
            nint pq = (nint)xq, pb = (nint)xb, pd = (nint)outDistances, pl = (nint)outLabels, pi = (nint)ids;
            Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
            {
                ScanOne<TOrder>((float*)pq + (long)q * d, (float*)pb, nb, d, k, metric,
                    (float*)pd + (long)q * k, (long*)pl + (long)q * k, (long*)pi, 0, sel);
            });
            return;
        }

        // Few queries, large database: split the database instead so a single query still scales.
        for (int q = 0; q < nq; q++)
            ScanOneBlockParallel<TOrder>(xq + (long)q * d, xb, nb, d, k, metric,
                outDistances + (long)q * k, outLabels + (long)q * k, ids, threads, sel);
    }

    /// <summary>Scans the whole database for one query into a single heap.</summary>
    private static void ScanOne<TOrder>(
        float* query, float* xb, int nb, int d, int k, MetricType metric,
        float* outDistances, long* outLabels, long* ids, long idOffset, IDSelector? sel)
        where TOrder : struct, IScoreOrder
    {
        if (sel is not null)
        {
            ScanOneFiltered<TOrder>(query, xb, nb, d, k, metric, outDistances, outLabels, ids, idOffset, sel);
            return;
        }

        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        for (int j = 0; j < nb; j++)
        {
            float score = VectorOps.Distance(query, xb + (long)j * d, d, metric);
            // One predictable branch rejects almost every candidate once the heap is full.
            if (TOrder.Better(score, heap.WorstScore))
                heap.Push(score, ids != null ? ids[j] : idOffset + j);
        }
        heap.Finish();
    }

    /// <summary>
    /// The filtered scan, kept out of <see cref="ScanOne{TOrder}"/> so the unfiltered hot loop stays
    /// exactly as small as it was before selectors existed.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ScanOneFiltered<TOrder>(
        float* query, float* xb, int nb, int d, int k, MetricType metric,
        float* outDistances, long* outLabels, long* ids, long idOffset, IDSelector sel)
        where TOrder : struct, IScoreOrder
    {
        var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
        for (int j = 0; j < nb; j++)
        {
            long id = ids != null ? ids[j] : idOffset + j;
            if (!sel.IsMember(id)) continue;
            float score = VectorOps.Distance(query, xb + (long)j * d, d, metric);
            if (TOrder.Better(score, heap.WorstScore)) heap.Push(score, id);
        }
        heap.Finish();
    }

    /// <summary>Scans one query with the database split across threads, then merges the partial heaps.</summary>
    private static void ScanOneBlockParallel<TOrder>(
        float* query, float* xb, int nb, int d, int k, MetricType metric,
        float* outDistances, long* outLabels, long* ids, int threads, IDSelector? sel)
        where TOrder : struct, IScoreOrder
    {
        int blocks = Math.Min(threads, Math.Max(1, nb / 1024));
        if (blocks <= 1)
        {
            ScanOne<TOrder>(query, xb, nb, d, k, metric, outDistances, outLabels, ids, 0, sel);
            return;
        }

        int blockSize = (nb + blocks - 1) / blocks;
        float[] partialDis = ArrayPool<float>.Shared.Rent(blocks * k);
        long[] partialIds = ArrayPool<long>.Shared.Rent(blocks * k);
        try
        {
            nint pq = (nint)query, pb = (nint)xb, pi = (nint)ids;
            fixed (float* pdis = partialDis)
            fixed (long* pids = partialIds)
            {
                nint pd = (nint)pdis, pl = (nint)pids;
                Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, b =>
                {
                    int start = b * blockSize;
                    int count = Math.Min(blockSize, nb - start);
                    if (count <= 0)
                    {
                        var empty = new KnnHeap<TOrder>(
                            new Span<float>((float*)pd + (long)b * k, k),
                            new Span<long>((long*)pl + (long)b * k, k));
                        empty.Finish();
                        return;
                    }
                    long* blockIds = (long*)pi;
                    ScanOne<TOrder>((float*)pq, (float*)pb + (long)start * d, count, d, k, metric,
                        (float*)pd + (long)b * k, (long*)pl + (long)b * k,
                        blockIds != null ? blockIds + start : null, start, sel);
                });

                var heap = new KnnHeap<TOrder>(new Span<float>(outDistances, k), new Span<long>(outLabels, k));
                for (int b = 0; b < blocks; b++)
                {
                    for (int i = 0; i < k; i++)
                    {
                        long id = pids[(long)b * k + i];
                        if (id < 0) break; // partial heaps are best-first, so -1 ends the block
                        heap.Push(pdis[(long)b * k + i], id);
                    }
                }
                heap.Finish();
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(partialDis);
            ArrayPool<long>.Shared.Return(partialIds);
        }
    }

    /// <summary>
    /// All database entries within <paramref name="radius"/> of each query. For distance metrics the
    /// test is <c>distance &lt; radius</c>; for inner product it is <c>similarity &gt; radius</c>,
    /// matching FAISS semantics.
    /// </summary>
    public static RangeSearchResult RangeSearch(
        float* xq, int nq, float* xb, int nb, int d, float radius,
        MetricType metric, long* ids = null, int maxThreads = 0, IDSelector? sel = null)
    {
        int threads = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        var perQuery = new List<(long Id, float Distance)>[nq];

        if (threads == 1 || (long)nq * nb < ParallelThreshold)
        {
            for (int q = 0; q < nq; q++)
                perQuery[q] = ScanRadius(xq + (long)q * d, xb, nb, d, radius, metric, ids, sel);
        }
        else
        {
            nint pq = (nint)xq, pb = (nint)xb, pi = (nint)ids;
            Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
            {
                perQuery[q] = ScanRadius((float*)pq + (long)q * d, (float*)pb, nb, d, radius, metric, (long*)pi, sel);
            });
        }

        return Flatten(perQuery);
    }

    /// <summary>Collects every database entry inside the radius for one query.</summary>
    private static List<(long Id, float Distance)> ScanRadius(
        float* query, float* xb, int nb, int d, float radius, MetricType metric, long* ids, IDSelector? sel)
    {
        var hits = new List<(long, float)>();
        bool similarity = metric.IsSimilarity();
        for (int j = 0; j < nb; j++)
        {
            long id = ids != null ? ids[j] : j;
            if (sel is not null && !sel.IsMember(id)) continue;
            float score = VectorOps.Distance(query, xb + (long)j * d, d, metric);
            if (similarity ? score > radius : score < radius)
                hits.Add((id, score));
        }
        return hits;
    }

    /// <summary>Packs per-query hit lists into the CSR layout of <see cref="RangeSearchResult"/>.</summary>
    public static RangeSearchResult Flatten(IReadOnlyList<List<(long Id, float Distance)>> perQuery)
    {
        int nq = perQuery.Count;
        var lims = new long[nq + 1];
        for (int q = 0; q < nq; q++) lims[q + 1] = lims[q] + (perQuery[q]?.Count ?? 0);

        long total = lims[nq];
        var labels = new long[total];
        var distances = new float[total];
        for (int q = 0; q < nq; q++)
        {
            var hits = perQuery[q];
            if (hits is null) continue;
            long offset = lims[q];
            for (int i = 0; i < hits.Count; i++)
            {
                labels[offset + i] = hits[i].Id;
                distances[offset + i] = hits[i].Distance;
            }
        }
        return new RangeSearchResult(lims, labels, distances);
    }
}
