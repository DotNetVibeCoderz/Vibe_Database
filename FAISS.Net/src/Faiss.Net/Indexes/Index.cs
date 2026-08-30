using Faiss.Net.Core;

namespace Faiss.Net;

/// <summary>
/// Base class for every index. The public surface deliberately mirrors <c>faiss.Index</c> in the
/// Python API — <c>Train</c>, <c>Add</c>, <c>Search</c>, <c>RangeSearch</c>, <c>Reconstruct</c>,
/// <c>RemoveIds</c>, <c>Reset</c> — so a Python program translates statement by statement.
/// <para>
/// Vectors are passed as flat row-major spans (<c>n * d</c> floats) exactly as NumPy passes a
/// contiguous 2-D array; <c>n</c> is inferred from the span length. Overloads taking
/// <c>float[][]</c> exist for convenience but copy into flat storage, so the span overloads are the
/// ones to use on hot paths.
/// </para>
/// </summary>
public abstract class Index
{
    /// <summary>Vector dimension. Named <c>d</c> in FAISS.</summary>
    public int D { get; protected set; }

    /// <summary>Vector dimension, spelled out for C# callers.</summary>
    public int Dimension => D;

    /// <summary>Number of indexed vectors.</summary>
    public long Ntotal { get; protected set; }

    /// <summary>Alias of <see cref="Ntotal"/>.</summary>
    public long Count => Ntotal;

    /// <summary>False until <see cref="Train"/> has run on indexes that need training.</summary>
    public bool IsTrained { get; protected set; } = true;

    /// <summary>Metric this index searches with.</summary>
    public MetricType MetricType { get; protected set; }

    /// <summary>
    /// Maximum worker threads for batch operations. <c>0</c> means every core. Set to <c>1</c> for
    /// deterministic timing in benchmarks or when the caller is already parallel.
    /// </summary>
    public int Threads { get; set; }

    /// <summary>True when this index stores vectors verbatim and can reconstruct them exactly.</summary>
    public virtual bool SupportsReconstruct => false;

    protected Index(int dimension, MetricType metric)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension), "Dimension must be positive.");
        D = dimension;
        MetricType = metric;
    }

    // ------------------------------------------------------------ Training

    /// <summary>
    /// Learns index parameters (centroids, codebooks, rotations) from a representative sample.
    /// A no-op for indexes that need no training, matching FAISS.
    /// </summary>
    public virtual void Train(ReadOnlySpan<float> x) { }

    /// <summary>Trains from jagged rows.</summary>
    public void Train(float[][] x) => Train(Flatten(x, D));

    // -------------------------------------------------------------- Adding

    /// <summary>Adds <c>x.Length / d</c> vectors with sequential ids starting at <see cref="Ntotal"/>.</summary>
    public abstract void Add(ReadOnlySpan<float> x);

    /// <summary>Adds jagged rows.</summary>
    public void Add(float[][] x) => Add(Flatten(x, D));

    /// <summary>Adds a single vector.</summary>
    public void AddOne(ReadOnlySpan<float> vector)
    {
        if (vector.Length != D) throw new ArgumentException($"Expected a vector of length {D}.");
        Add(vector);
    }

    /// <summary>
    /// Adds vectors under caller-chosen ids. Only indexes that maintain an id table support this;
    /// wrap any other index in <see cref="IndexIDMap"/>, the same requirement as in FAISS.
    /// </summary>
    public virtual void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support explicit ids. Wrap it: new IndexIDMap(index).AddWithIds(x, ids).");

    // ------------------------------------------------------------ Searching

    /// <summary>
    /// k nearest neighbours of each query. Equivalent to Python's <c>D, I = index.search(xq, k)</c>.
    /// </summary>
    /// <param name="queries">Flat <c>nq * d</c> query vectors; a single query is just <c>d</c> floats.</param>
    /// <param name="k">Neighbours per query.</param>
    public SearchResult Search(ReadOnlySpan<float> queries, int k)
    {
        int nq = ValidateBatch(queries, nameof(queries));
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");
        var result = SearchResult.Allocate(nq, k);
        Search(queries, nq, k, result.Distances, result.Labels);
        return result;
    }

    /// <summary>Searches from jagged query rows.</summary>
    public SearchResult Search(float[][] queries, int k) => Search(Flatten(queries, D), k);

    /// <summary>
    /// Core search, writing into caller-owned buffers. Use this in loops and servers to avoid
    /// allocating a result object per request.
    /// </summary>
    /// <param name="distances">Output buffer of <c>nq * k</c> scores, best first per query.</param>
    /// <param name="labels">Output buffer of <c>nq * k</c> ids, <c>-1</c> where no neighbour exists.</param>
    public abstract void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels);

    /// <summary>
    /// Every neighbour within <paramref name="radius"/>. For distance metrics the test is
    /// <c>distance &lt; radius</c>; for inner product it is <c>similarity &gt; radius</c>.
    /// </summary>
    public virtual RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        throw new NotSupportedException($"{GetType().Name} does not implement range search.");

    /// <summary>Radius search from jagged query rows.</summary>
    public RangeSearchResult RangeSearch(float[][] queries, float radius) =>
        RangeSearch(Flatten(queries, D), radius);

    // ------------------------------------------------------------- Removal

    /// <summary>Removes the listed ids. Returns how many were actually removed.</summary>
    public virtual long RemoveIds(ReadOnlySpan<long> ids) =>
        throw new NotSupportedException($"{GetType().Name} does not support removal.");

    /// <summary>Removes every id matching a predicate, the <c>IDSelector</c> form from FAISS.</summary>
    public virtual long RemoveIds(Func<long, bool> predicate) =>
        throw new NotSupportedException($"{GetType().Name} does not support removal.");

    /// <summary>Removes a single id.</summary>
    public long RemoveId(long id) => RemoveIds([id]);

    /// <summary>Drops every vector, keeping training state, like <c>index.reset()</c>.</summary>
    public abstract void Reset();

    // ------------------------------------------------------- Reconstruction

    /// <summary>
    /// Recovers the stored vector for an id. Compressed indexes return the decoded approximation,
    /// which is the point of <c>reconstruct</c> in FAISS: it shows what the index actually kept.
    /// </summary>
    public virtual void Reconstruct(long key, Span<float> output) =>
        throw new NotSupportedException($"{GetType().Name} does not support reconstruction.");

    /// <summary>Allocating form of <see cref="Reconstruct(long, Span{float})"/>.</summary>
    public float[] Reconstruct(long key)
    {
        var output = new float[D];
        Reconstruct(key, output);
        return output;
    }

    /// <summary>Reconstructs <paramref name="n"/> consecutive vectors into a flat buffer.</summary>
    public virtual void ReconstructN(long start, long n, Span<float> output)
    {
        for (long i = 0; i < n; i++)
            Reconstruct(start + i, output.Slice((int)(i * D), D));
    }

    // ------------------------------------------------------------- Utility

    /// <summary>Approximate resident bytes held by this index. Drives the memory columns in the samples.</summary>
    public virtual long MemoryUsage => 0;

    /// <summary>Short human-readable description, e.g. <c>IndexIVFPQ(d=128, nlist=1024, m=16, L2)</c>.</summary>
    public virtual string Describe() => $"{GetType().Name}(d={D}, ntotal={Ntotal}, {MetricType.ToShortString()})";

    public override string ToString() => Describe();

    // ------------------------------------------------------------ Internals

    /// <summary>Validates a flat batch and returns the vector count.</summary>
    protected int ValidateBatch(ReadOnlySpan<float> x, string paramName)
    {
        if (x.Length == 0) return 0;
        if (x.Length % D != 0)
            throw new ArgumentException(
                $"Input length {x.Length} is not a multiple of dimension {D}.", paramName);
        return x.Length / D;
    }

    /// <summary>Throws when the index still needs training, with the call the caller is missing.</summary>
    protected void EnsureTrained()
    {
        if (!IsTrained)
            throw new InvalidOperationException(
                $"{GetType().Name} must be trained before use. Call Train(trainingVectors) first.");
    }

    /// <summary>Copies jagged rows into one flat row-major buffer.</summary>
    protected internal static float[] Flatten(float[][] rows, int d)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var flat = new float[(long)rows.Length * d];
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Length != d)
                throw new ArgumentException($"Row {i} has length {rows[i].Length}, expected {d}.");
            rows[i].CopyTo(flat, (long)i * d);
        }
        return flat;
    }

    // --------------------------------------------------------- Serialization

    /// <summary>Type tag written to disk; see <see cref="IO.IndexTypeCode"/>.</summary>
    protected internal virtual IO.IndexTypeCode TypeCode =>
        throw new NotSupportedException($"{GetType().Name} cannot be serialized.");

    /// <summary>Writes index-specific state after the common header.</summary>
    protected internal virtual void WriteBody(BinaryWriter writer) =>
        throw new NotSupportedException($"{GetType().Name} cannot be serialized.");

    /// <summary>Reads index-specific state written by <see cref="WriteBody"/>.</summary>
    protected internal virtual void ReadBody(BinaryReader reader) =>
        throw new NotSupportedException($"{GetType().Name} cannot be deserialized.");

    /// <summary>Lets the IO layer restore header fields on a freshly constructed instance.</summary>
    protected internal void RestoreHeader(long ntotal, bool isTrained)
    {
        Ntotal = ntotal;
        IsTrained = isTrained;
    }
}
