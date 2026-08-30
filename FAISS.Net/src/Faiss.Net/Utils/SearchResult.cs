namespace Faiss.Net;

/// <summary>
/// Result of a k-nearest-neighbour search: the <c>(D, I)</c> tuple returned by Python FAISS,
/// as one object with row-major <c>n x k</c> buffers.
/// <para>
/// Rows are queries, columns are ranks (best first). When fewer than k neighbours exist, trailing
/// slots carry label <c>-1</c> and a sentinel distance, exactly as FAISS does. Deconstruction keeps
/// the Python shape available:
/// <code>var (distances, labels) = index.Search(query, k: 10);</code>
/// </para>
/// </summary>
public sealed class SearchResult
{
    /// <summary>Flat <c>n x k</c> distances (or similarities for inner product), best first per row.</summary>
    public float[] Distances { get; }

    /// <summary>Flat <c>n x k</c> ids; <c>-1</c> marks an empty slot.</summary>
    public long[] Labels { get; }

    /// <summary>Number of queries.</summary>
    public int QueryCount { get; }

    /// <summary>Neighbours requested per query.</summary>
    public int K { get; }

    public SearchResult(float[] distances, long[] labels, int queryCount, int k)
    {
        Distances = distances;
        Labels = labels;
        QueryCount = queryCount;
        K = k;
    }

    /// <summary>Allocates an empty result sized for <paramref name="queryCount"/> x <paramref name="k"/>.</summary>
    public static SearchResult Allocate(int queryCount, int k) =>
        new(new float[(long)queryCount * k], new long[(long)queryCount * k], queryCount, k);

    /// <summary>Distances for one query, best first.</summary>
    public ReadOnlySpan<float> DistancesFor(int query) => Distances.AsSpan(query * K, K);

    /// <summary>Labels for one query, best first.</summary>
    public ReadOnlySpan<long> LabelsFor(int query) => Labels.AsSpan(query * K, K);

    /// <summary>Single neighbour by query and rank.</summary>
    public (long Id, float Distance) this[int query, int rank] =>
        (Labels[query * K + rank], Distances[query * K + rank]);

    /// <summary>Neighbours of one query as a sequence, stopping at the first empty slot.</summary>
    public IEnumerable<(long Id, float Distance)> Neighbors(int query = 0)
    {
        for (int rank = 0; rank < K; rank++)
        {
            long id = Labels[query * K + rank];
            if (id < 0) yield break;
            yield return (id, Distances[query * K + rank]);
        }
    }

    /// <summary>Enables <c>var (distances, labels) = index.Search(...)</c>, mirroring Python's <c>D, I</c>.</summary>
    public void Deconstruct(out float[] distances, out long[] labels)
    {
        distances = Distances;
        labels = Labels;
    }
}

/// <summary>
/// Result of a radius search. Because each query returns a different number of hits, results are
/// stored CSR-style: query <c>i</c> owns <c>Labels[Lims[i] .. Lims[i + 1]]</c>. This is the same
/// layout as <c>faiss.RangeSearchResult</c>.
/// </summary>
public sealed class RangeSearchResult
{
    /// <summary>Row offsets, length <c>QueryCount + 1</c>.</summary>
    public long[] Lims { get; }

    /// <summary>Concatenated ids for all queries.</summary>
    public long[] Labels { get; }

    /// <summary>Concatenated distances, aligned with <see cref="Labels"/>.</summary>
    public float[] Distances { get; }

    /// <summary>Number of queries.</summary>
    public int QueryCount => Lims.Length - 1;

    /// <summary>Total number of hits across all queries.</summary>
    public long TotalResults => Lims[^1];

    public RangeSearchResult(long[] lims, long[] labels, float[] distances)
    {
        Lims = lims;
        Labels = labels;
        Distances = distances;
    }

    /// <summary>Ids matched by one query. Order is unspecified, as in FAISS.</summary>
    public ReadOnlySpan<long> LabelsFor(int query) =>
        Labels.AsSpan((int)Lims[query], (int)(Lims[query + 1] - Lims[query]));

    /// <summary>Distances matched by one query, aligned with <see cref="LabelsFor"/>.</summary>
    public ReadOnlySpan<float> DistancesFor(int query) =>
        Distances.AsSpan((int)Lims[query], (int)(Lims[query + 1] - Lims[query]));

    /// <summary>Hits of one query as a sequence.</summary>
    public IEnumerable<(long Id, float Distance)> Matches(int query = 0)
    {
        for (long i = Lims[query]; i < Lims[query + 1]; i++)
            yield return (Labels[i], Distances[i]);
    }
}
