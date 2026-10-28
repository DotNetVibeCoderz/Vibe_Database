using System.Buffers;
using Faiss.Net.Utils;

namespace Faiss.Net.Sparse;

/// <summary>
/// A sparse vector: the non-zero coordinates of a vector in a (usually very large) space, as
/// parallel index and value arrays. Indices must be distinct; they need not be sorted.
/// </summary>
public readonly record struct SparseVector(int[] Indices, float[] Values)
{
    /// <summary>Number of non-zero coordinates.</summary>
    public int Count => Indices.Length;

    /// <summary>Builds a sparse vector from a dense one, keeping coordinates whose magnitude exceeds <paramref name="threshold"/>.</summary>
    public static SparseVector FromDense(ReadOnlySpan<float> dense, float threshold = 0f)
    {
        var indices = new List<int>();
        var values = new List<float>();
        for (int i = 0; i < dense.Length; i++)
        {
            if (MathF.Abs(dense[i]) <= threshold) continue;
            indices.Add(i);
            values.Add(dense[i]);
        }
        return new SparseVector([.. indices], [.. values]);
    }

    /// <summary>Exact inner product with another sparse vector.</summary>
    public float Dot(SparseVector other)
    {
        var lookup = new Dictionary<int, float>(Count);
        for (int i = 0; i < Count; i++) lookup[Indices[i]] = Values[i];
        float sum = 0;
        for (int i = 0; i < other.Count; i++)
            if (lookup.TryGetValue(other.Indices[i], out float v)) sum += v * other.Values[i];
        return sum;
    }
}

/// <summary>
/// Exact maximum-inner-product search over sparse vectors — learned sparse embeddings (SPLADE),
/// weighted bag-of-words, or any vector with a few hundred non-zeros out of tens of thousands of
/// dimensions.
/// <para>
/// The dense indexes cannot serve these: a 30,000-dimensional vector with 100 non-zeros would be
/// stored and scanned as 30,000 floats. Here vectors live in an inverted index — one posting list per
/// dimension, holding the (vector, value) pairs that use it — and a query is scored term at a time:
/// for each of its non-zero dimensions, walk that dimension's postings and accumulate
/// <c>query value * stored value</c> into a per-vector score. Cost is proportional to the postings
/// the query actually touches, not to the database size times the dimension.
/// </para>
/// <para>
/// Scores are exact inner products; normalize vectors on the way in (<see cref="NormalizeOnAdd"/>)
/// for cosine similarity. Removal marks vectors deleted and keeps ids stable; deleted vectors are
/// skipped at search time and dropped from postings by <see cref="Compact"/>. Searches may run while
/// one writer adds, as for the dense flat index: a search scores the vectors that existed when it
/// started.
/// </para>
/// </summary>
public sealed class IndexSparseFlat
{
    private sealed class Posting
    {
        public int[] Ids = [];
        public float[] Values = [];
        public int Count;
    }

    private sealed class State(Posting?[] postings, int count, bool[] deleted)
    {
        public readonly Posting?[] Postings = postings;
        public readonly int Count = count;
        public readonly bool[] Deleted = deleted;
    }

    private State _state;
    private readonly List<SparseVector> _forward = [];

    /// <summary>Dimension of the space (vocabulary size); indices must lie in <c>[0, Dimension)</c>.</summary>
    public int Dimension { get; }

    /// <summary>When true, vectors are L2-normalized as they are added, making scores cosine similarities.</summary>
    public bool NormalizeOnAdd { get; }

    /// <summary>Vectors added, including deleted ones (ids are positions and never reused).</summary>
    public long Ntotal => Volatile.Read(ref _state).Count;

    /// <summary>Worker threads for batch search; 0 means every core.</summary>
    public int Threads { get; set; }

    public IndexSparseFlat(int dimension, bool normalizeOnAdd = false)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        Dimension = dimension;
        NormalizeOnAdd = normalizeOnAdd;
        _state = new State(new Posting?[dimension], 0, []);
    }

    // -------------------------------------------------------------- Adding

    /// <summary>Adds vectors with sequential ids starting at <see cref="Ntotal"/>.</summary>
    public void Add(IReadOnlyList<SparseVector> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0) return;
        var state = _state;
        int first = state.Count;

        // Postings are append-only per dimension: a list with room is written past its published
        // count, and one without room is copied into a new array. A reader holding the old State
        // stops at the first id beyond that state's Count, so neither case disturbs it.
        var prepared = new SparseVector[vectors.Count];
        var additions = new Dictionary<int, int>();
        for (int v = 0; v < vectors.Count; v++)
        {
            var vector = Validate(vectors[v]);
            if (NormalizeOnAdd) vector = Normalize(vector);
            prepared[v] = vector;
            foreach (int dim in vector.Indices) additions[dim] = additions.GetValueOrDefault(dim) + 1;
        }

        var postings = (Posting?[])state.Postings.Clone();
        var counts = new Dictionary<int, int>(additions.Count);
        foreach (var (dim, extra) in additions)
        {
            var existing = postings[dim];
            int count = existing?.Count ?? 0;
            if (existing is null || count + extra > existing.Ids.Length) postings[dim] = Grow(existing, extra);
            counts[dim] = count;
        }

        for (int v = 0; v < prepared.Length; v++)
        {
            var vector = prepared[v];
            for (int i = 0; i < vector.Count; i++)
            {
                int dim = vector.Indices[i];
                var posting = postings[dim]!;
                int slot = counts[dim]++;
                posting.Ids[slot] = first + v;
                posting.Values[slot] = vector.Values[i];
            }
        }
        // Counts are published after the entries they cover (release), so a reader that sees a
        // longer list also sees the ids and values in it.
        foreach (var (dim, count) in counts) Volatile.Write(ref postings[dim]!.Count, count);

        _forward.AddRange(prepared);
        var deleted = new bool[first + vectors.Count];
        state.Deleted.CopyTo(deleted, 0);
        Volatile.Write(ref _state, new State(postings, first + vectors.Count, deleted));
    }

    /// <summary>Adds one vector.</summary>
    public void Add(SparseVector vector) => Add([vector]);


    /// <summary>A posting list with room for <paramref name="additional"/> more entries, copied so readers keep the old one.</summary>
    private static Posting Grow(Posting? existing, int additional)
    {
        int count = existing?.Count ?? 0;
        int capacity = Math.Max(count + additional, Math.Max(4, count + (count >> 1)));
        var grown = new Posting { Ids = new int[capacity], Values = new float[capacity], Count = count };
        if (existing is not null)
        {
            existing.Ids.AsSpan(0, count).CopyTo(grown.Ids);
            existing.Values.AsSpan(0, count).CopyTo(grown.Values);
        }
        return grown;
    }

    private SparseVector Validate(SparseVector vector)
    {
        if (vector.Indices is null || vector.Values is null || vector.Indices.Length != vector.Values.Length)
            throw new ArgumentException("A sparse vector needs index and value arrays of equal length.");
        var seen = new HashSet<int>();
        foreach (int index in vector.Indices)
        {
            if ((uint)index >= (uint)Dimension)
                throw new ArgumentOutOfRangeException(nameof(vector), $"Index {index} is outside [0, {Dimension}).");
            if (!seen.Add(index)) throw new ArgumentException($"Index {index} appears twice in one sparse vector.");
        }
        return vector;
    }

    private static SparseVector Normalize(SparseVector vector)
    {
        double norm = 0;
        foreach (float v in vector.Values) norm += (double)v * v;
        if (norm == 0) return vector;
        float scale = (float)(1 / Math.Sqrt(norm));
        return vector with { Values = vector.Values.Select(v => v * scale).ToArray() };
    }

    // ------------------------------------------------------------ Searching

    /// <summary>The k vectors with the largest inner product with each query.</summary>
    /// <param name="queries">Query vectors.</param>
    /// <param name="k">Results per query; missing results are id -1.</param>
    /// <param name="selector">Optional filter over ids.</param>
    public SearchResult Search(IReadOnlyList<SparseVector> queries, int k, IDSelector? selector = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        var state = Volatile.Read(ref _state);
        int nq = queries.Count;
        var result = SearchResult.Allocate(nq, k);
        int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

        Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = nq == 1 ? 1 : threads }, q =>
        {
            var query = Validate(queries[q]);
            if (NormalizeOnAdd) query = Normalize(query);
            SearchOne(state, query, k, result.Distances.AsSpan(q * k, k), result.Labels.AsSpan(q * k, k), selector);
        });
        return result;
    }

    /// <summary>The k best matches for one query.</summary>
    public SearchResult Search(SparseVector query, int k, IDSelector? selector = null) => Search([query], k, selector);

    private static void SearchOne(State state, SparseVector query, int k, Span<float> distances, Span<long> labels, IDSelector? sel)
    {
        float[] scores = ArrayPool<float>.Shared.Rent(Math.Max(1, state.Count));
        int[] touched = ArrayPool<int>.Shared.Rent(Math.Max(1, state.Count));
        bool[] seen = ArrayPool<bool>.Shared.Rent(Math.Max(1, state.Count));
        seen.AsSpan(0, state.Count).Clear();
        int touchedCount = 0;
        try
        {
            // Term at a time: every posting of every query dimension adds into its vector's score,
            // and the touched list records which scores are live so only those are ranked.
            for (int i = 0; i < query.Count; i++)
            {
                var posting = state.Postings[query.Indices[i]];
                if (posting is null) continue;
                float weight = query.Values[i];
                int length = Volatile.Read(ref posting.Count);
                for (int p = 0; p < length; p++)
                {
                    int id = posting.Ids[p];
                    if (id >= state.Count) break; // appended after this search's snapshot
                    if (!seen[id])
                    {
                        seen[id] = true;
                        scores[id] = 0;
                        touched[touchedCount++] = id;
                    }
                    scores[id] += weight * posting.Values[p];
                }
            }

            var heap = new KnnHeap<DescendingOrder>(distances, labels);
            for (int t = 0; t < touchedCount; t++)
            {
                int id = touched[t];
                if (state.Deleted[id]) continue;
                if (sel is not null && !sel.IsMember(id)) continue;
                if (scores[id] > heap.WorstScore) heap.Push(scores[id], id);
            }
            heap.Finish();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scores);
            ArrayPool<int>.Shared.Return(touched);
            ArrayPool<bool>.Shared.Return(seen);
        }
    }

    // ------------------------------------------------------------- Removal

    /// <summary>Marks vectors deleted. Ids stay stable; deleted vectors never appear in results.</summary>
    public long RemoveIds(IEnumerable<long> ids)
    {
        var state = _state;
        var deleted = (bool[])state.Deleted.Clone();
        long removed = 0;
        foreach (long id in ids)
            if (id >= 0 && id < state.Count && !deleted[id]) { deleted[id] = true; removed++; }
        Volatile.Write(ref _state, new State(state.Postings, state.Count, deleted));
        return removed;
    }

    /// <summary>Vectors currently marked deleted.</summary>
    public long DeletedCount => Volatile.Read(ref _state).Deleted.Count(d => d);

    /// <summary>Drops deleted vectors from the posting lists, reclaiming their memory. Ids are unchanged.</summary>
    public void Compact()
    {
        var state = _state;
        var postings = new Posting?[Dimension];
        for (int dim = 0; dim < Dimension; dim++)
        {
            var old = state.Postings[dim];
            if (old is null) continue;
            var kept = new Posting { Ids = new int[old.Count], Values = new float[old.Count] };
            for (int p = 0; p < old.Count; p++)
            {
                if (state.Deleted[old.Ids[p]]) continue;
                kept.Ids[kept.Count] = old.Ids[p];
                kept.Values[kept.Count] = old.Values[p];
                kept.Count++;
            }
            if (kept.Count > 0) postings[dim] = kept;
        }
        Volatile.Write(ref _state, new State(postings, state.Count, state.Deleted));
    }

    /// <summary>The stored vector (normalized, if <see cref="NormalizeOnAdd"/> is set).</summary>
    public SparseVector Reconstruct(long id)
    {
        if (id < 0 || id >= Ntotal) throw new ArgumentOutOfRangeException(nameof(id));
        return _forward[(int)id];
    }

    /// <summary>Drops every vector.</summary>
    public void Reset()
    {
        _forward.Clear();
        Volatile.Write(ref _state, new State(new Posting?[Dimension], 0, []));
    }

    /// <summary>Approximate resident bytes: postings plus the forward copy used by <see cref="Reconstruct"/>.</summary>
    public long MemoryUsage
    {
        get
        {
            var state = Volatile.Read(ref _state);
            long total = (long)Dimension * IntPtr.Size + state.Deleted.Length;
            foreach (var posting in state.Postings)
                if (posting is not null) total += posting.Ids.Length * (sizeof(int) + sizeof(float));
            foreach (var vector in _forward) total += vector.Count * (sizeof(int) + sizeof(float));
            return total;
        }
    }

    public override string ToString() => $"IndexSparseFlat(dim={Dimension}, ntotal={Ntotal}, deleted={DeletedCount})";

    // -------------------------------------------------------- Serialization

    /// <summary>Writes the index; the inverted lists are rebuilt from the forward vectors on load.</summary>
    public void Write(BinaryWriter writer)
    {
        var state = Volatile.Read(ref _state);
        writer.Write(Dimension);
        writer.Write(NormalizeOnAdd);
        writer.Write(state.Count);
        for (int id = 0; id < state.Count; id++)
        {
            var vector = _forward[id];
            writer.Write(state.Deleted[id]);
            writer.Write(vector.Count);
            foreach (int index in vector.Indices) writer.Write(index);
            foreach (float value in vector.Values) writer.Write(value);
        }
    }

    /// <summary>Reads an index written by <see cref="Write"/>.</summary>
    public static IndexSparseFlat Read(BinaryReader reader)
    {
        int dimension = reader.ReadInt32();
        bool normalize = reader.ReadBoolean();
        int count = reader.ReadInt32();
        // Vectors were stored already normalized; adding them through a non-normalizing index and
        // then restoring the flag avoids normalizing twice.
        var index = new IndexSparseFlat(dimension, normalizeOnAdd: false);
        var vectors = new SparseVector[count];
        var deletedIds = new List<long>();
        for (int id = 0; id < count; id++)
        {
            if (reader.ReadBoolean()) deletedIds.Add(id);
            int nnz = reader.ReadInt32();
            var indices = new int[nnz];
            var values = new float[nnz];
            for (int i = 0; i < nnz; i++) indices[i] = reader.ReadInt32();
            for (int i = 0; i < nnz; i++) values[i] = reader.ReadSingle();
            vectors[id] = new SparseVector(indices, values);
        }
        index.Add(vectors);
        index.RemoveIds(deletedIds);
        return normalize ? index.WithNormalizeFlag() : index;
    }

    private IndexSparseFlat WithNormalizeFlag()
    {
        var copy = new IndexSparseFlat(Dimension, normalizeOnAdd: true);
        copy._forward.AddRange(_forward);
        copy._state = _state;
        return copy;
    }
}
