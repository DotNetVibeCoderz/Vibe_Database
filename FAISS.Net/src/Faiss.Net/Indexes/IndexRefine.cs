using System.Buffers;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Two-stage search: a fast, compressed base index proposes <c>k * KFactor</c> candidates, and a
/// precise refine index re-scores just those — the FAISS <c>IndexRefine</c>.
/// <para>
/// This recovers most of what quantization costs. An <see cref="IndexIVFPQ"/> at <c>m = 16</c> gives
/// up about thirty points of recall@10 on its own; re-ranking its top 40 against the original
/// vectors brings it back above ninety while the scan still reads only the 16-byte codes. Only the
/// re-scored candidates touch the refine index, so its memory can live in a slower tier (see
/// <see cref="IO.MappedIndexFlat"/>) without the scan paying for it.
/// </para>
/// <para>
/// Both indexes receive every vector in the same order, so a base result id is a position in the
/// refine index. That holds for every index that assigns sequential ids; do not wrap the base in an
/// <see cref="IndexIDMap"/> — wrap the <see cref="IndexRefine"/> instead.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var index = new IndexRefine(new IndexIVFPQ(d, 1024, m: 16), new IndexFlatL2(d)) { KFactor = 4 };
/// index.Train(sample);
/// index.Add(vectors);
/// var result = index.Search(query, k: 10);
/// </code>
/// </example>
public class IndexRefine : Index
{
    /// <summary>The fast first-stage index. Named <c>base_index</c> in FAISS.</summary>
    public Index BaseIndex { get; private set; }

    /// <summary>The precise re-scoring index. Named <c>refine_index</c> in FAISS.</summary>
    public Index RefineIndex { get; private set; }

    /// <summary>
    /// Candidates fetched per requested result. 1 re-ranks without widening; 4 to 10 is the usual
    /// range. Cost is <c>k * KFactor</c> exact distances per query on top of the base search.
    /// </summary>
    public float KFactor { get; set; } = 1;

    public IndexRefine(Index baseIndex, Index refineIndex) : base(CheckDimension(baseIndex, refineIndex), baseIndex.MetricType)
    {
        if (refineIndex.MetricType != baseIndex.MetricType)
            throw new ArgumentException(
                $"Base metric {baseIndex.MetricType} and refine metric {refineIndex.MetricType} differ; re-ranking would mix scales.");
        BaseIndex = baseIndex;
        RefineIndex = refineIndex;
        Ntotal = baseIndex.Ntotal;
        IsTrained = baseIndex.IsTrained && refineIndex.IsTrained;
    }

    private static int CheckDimension(Index baseIndex, Index refineIndex)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(refineIndex);
        if (baseIndex.D != refineIndex.D)
            throw new ArgumentException($"Base dimension {baseIndex.D} and refine dimension {refineIndex.D} differ.");
        return baseIndex.D;
    }

    public override bool SupportsReconstruct => RefineIndex.SupportsReconstruct;

    public override void Train(ReadOnlySpan<float> x)
    {
        BaseIndex.Train(x);
        RefineIndex.Train(x);
        IsTrained = BaseIndex.IsTrained && RefineIndex.IsTrained;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        // Refine first: a concurrent search that sees a new base id then always finds its vector.
        RefineIndex.Add(x);
        BaseIndex.Add(x);
        Ntotal = BaseIndex.Ntotal;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels,
        SearchParameters? parameters)
    {
        EnsureTrained();
        if (nq == 0) return;

        var refineParams = parameters as SearchParametersRefine;
        float factor = refineParams is { KFactor: > 0 } ? refineParams.KFactor : KFactor;
        int kBase = Math.Max(k, (int)Math.Ceiling(k * Math.Max(1f, factor)));

        // The selector is applied by the base search; the refine stage only re-scores survivors.
        SearchParameters? baseParams = refineParams?.BaseIndexParams;
        if (parameters?.Sel is not null)
            baseParams = SearchParametersCopy.WithSelector(baseParams, parameters.Sel);

        var candidates = SearchResult.Allocate(nq, kBase);
        BaseIndex.Search(queries, nq, kBase, candidates.Distances, candidates.Labels, baseParams);

        bool similarity = MetricType.IsSimilarity();
        float worst = similarity ? float.MinValue : float.MaxValue;
        var refined = new float[(long)nq * kBase];

        var queryArray = queries[..(nq * D)].ToArray();
        int threads = Threads > 0 ? Threads : Environment.ProcessorCount;
        Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = nq == 1 ? 1 : threads }, q =>
            RefineIndex.ComputeDistanceSubset(
                queryArray.AsSpan(q * D, D), candidates.Labels.AsSpan(q * kBase, kBase), refined.AsSpan(q * kBase, kBase)));

        var order = ArrayPool<int>.Shared.Rent(kBase);
        try
        {
            for (int q = 0; q < nq; q++)
            {
                var ids = candidates.Labels.AsSpan(q * kBase, kBase);
                var scores = refined.AsSpan(q * kBase, kBase);
                int valid = 0;
                for (int i = 0; i < kBase; i++) if (ids[i] >= 0) order[valid++] = i;

                // Sort candidate positions by refined score, best first.
                var slice = order.AsSpan(0, valid);
                var scoreArray = refined;
                int baseOffset = q * kBase;
                slice.Sort((a, b) => similarity
                    ? scoreArray[baseOffset + b].CompareTo(scoreArray[baseOffset + a])
                    : scoreArray[baseOffset + a].CompareTo(scoreArray[baseOffset + b]));

                var outD = distances.Slice(q * k, k);
                var outL = labels.Slice(q * k, k);
                int count = Math.Min(k, valid);
                for (int i = 0; i < count; i++)
                {
                    outD[i] = scores[slice[i]];
                    outL[i] = ids[slice[i]];
                }
                for (int i = count; i < k; i++)
                {
                    outD[i] = worst;
                    outL[i] = -1;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
        }
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        RangeSearch(queries, radius, parameters: null);

    /// <summary>
    /// Range search on the base index, with each hit's distance recomputed by the refine index and
    /// hits outside the radius after refinement dropped. Matches that the base index misses are not
    /// recovered; widen the base search (larger nprobe or efSearch) for that.
    /// </summary>
    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius, SearchParameters? parameters)
    {
        EnsureTrained();
        int nq = ValidateBatch(queries, nameof(queries));
        var coarse = BaseIndex.RangeSearch(queries, radius, parameters: parameters);
        bool similarity = MetricType.IsSimilarity();

        var perQuery = new List<(long Id, float Distance)>[nq];
        for (int q = 0; q < nq; q++)
        {
            var ids = coarse.LabelsFor(q);
            var scores = new float[ids.Length];
            RefineIndex.ComputeDistanceSubset(queries.Slice(q * D, D), ids, scores);
            var hits = new List<(long, float)>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
                if (similarity ? scores[i] > radius : scores[i] < radius) hits.Add((ids[i], scores[i]));
            perQuery[q] = hits;
        }
        return Core.BruteForce.Flatten(perQuery);
    }

    public override void Reconstruct(long key, Span<float> output) => RefineIndex.Reconstruct(key, output);

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        var drop = new HashSet<long>();
        foreach (long id in ids) drop.Add(id);
        return RemoveIds(drop.Contains);
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        long removed = BaseIndex.RemoveIds(predicate);
        long refined = RefineIndex.RemoveIds(predicate);
        if (removed != refined)
            throw new InvalidOperationException(
                $"Base removed {removed} vectors but refine removed {refined}; the two indexes are no longer aligned.");
        Ntotal = BaseIndex.Ntotal;
        return removed;
    }

    public override void Reset()
    {
        BaseIndex.Reset();
        RefineIndex.Reset();
        Ntotal = 0;
    }

    public override long MemoryUsage => BaseIndex.MemoryUsage + RefineIndex.MemoryUsage;

    public override string Describe() =>
        $"{GetType().Name}(kFactor={KFactor}, base={BaseIndex.Describe()}, refine={RefineIndex.Describe()})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.Refine;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(KFactor);
        IndexIO.WriteTo(writer, BaseIndex);
        IndexIO.WriteTo(writer, RefineIndex);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        KFactor = reader.ReadSingle();
        BaseIndex = IndexIO.ReadFrom(reader);
        RefineIndex = IndexIO.ReadFrom(reader);
        Ntotal = BaseIndex.Ntotal;
        IsTrained = BaseIndex.IsTrained && RefineIndex.IsTrained;
    }

    internal static IndexRefine CreateForRead(int dimension, MetricType metric) =>
        new(new IndexFlat(dimension, metric), new IndexFlat(dimension, metric));
}

/// <summary>
/// <see cref="IndexRefine"/> whose refine stage is an exact <see cref="IndexFlat"/> of the same
/// metric, created for you — the FAISS <c>IndexRefineFlat</c>, and the <c>RFlat</c> suffix of a
/// factory string such as <c>"IVF1024,PQ16,RFlat"</c>.
/// </summary>
public sealed class IndexRefineFlat : IndexRefine
{
    public IndexRefineFlat(Index baseIndex) : base(baseIndex, new IndexFlat(baseIndex.D, baseIndex.MetricType)) { }

    private IndexRefineFlat(int dimension, MetricType metric) : base(new IndexFlat(dimension, metric), new IndexFlat(dimension, metric)) { }

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.RefineFlat;

    internal static new IndexRefineFlat CreateForRead(int dimension, MetricType metric) => new(dimension, metric);
}
