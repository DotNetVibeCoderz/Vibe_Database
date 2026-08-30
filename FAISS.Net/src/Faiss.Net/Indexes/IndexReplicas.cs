using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Holds the same data in several sub-indexes and splits each query batch across them.
/// <para>
/// This is the multi-GPU pattern from FAISS: put one replica on each device, and query throughput
/// scales with device count while latency for a single query is unchanged. It is equally useful on
/// CPU to pin replicas to NUMA nodes. Every replica must contain identical data, which the wrapper
/// enforces by broadcasting <see cref="Add"/> and <see cref="Train"/> to all of them.
/// </para>
/// </summary>
public sealed class IndexReplicas : Index
{
    private readonly List<Index> _replicas = [];

    /// <summary>The replicas, in the order they were added.</summary>
    public IReadOnlyList<Index> Replicas => _replicas;

    public IndexReplicas(int dimension, MetricType metric = MetricType.L2) : base(dimension, metric) { }

    /// <summary>Adds a replica. It must match the wrapper's dimension and metric.</summary>
    public void AddReplica(Index index)
    {
        if (index.D != D) throw new ArgumentException($"Replica dimension {index.D} does not match {D}.");
        if (index.MetricType != MetricType)
            throw new ArgumentException($"Replica metric {index.MetricType} does not match {MetricType}.");
        _replicas.Add(index);
        IsTrained = _replicas.TrueForAll(r => r.IsTrained);
        Ntotal = _replicas.Count > 0 ? _replicas[0].Ntotal : 0;
    }

    public override void Train(ReadOnlySpan<float> x)
    {
        // Training is deterministic given the same input, so every replica ends up identical.
        foreach (var replica in _replicas) replica.Train(x);
        IsTrained = true;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        foreach (var replica in _replicas) replica.Add(x);
        Ntotal = _replicas.Count > 0 ? _replicas[0].Ntotal : 0;
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        foreach (var replica in _replicas) replica.AddWithIds(x, ids);
        Ntotal = _replicas.Count > 0 ? _replicas[0].Ntotal : 0;
    }

    public override unsafe void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (_replicas.Count == 0) throw new InvalidOperationException("IndexReplicas has no replicas.");
        if (_replicas.Count == 1 || nq == 1)
        {
            _replicas[0].Search(queries, nq, k, distances, labels);
            return;
        }

        // Contiguous slices rather than round-robin, so each replica sees a cache-friendly run.
        int replicaCount = _replicas.Count;
        int chunk = (nq + replicaCount - 1) / replicaCount;

        fixed (float* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int d = D;
            Parallel.For(0, replicaCount, r =>
            {
                int start = r * chunk;
                int count = Math.Min(chunk, nq - start);
                if (count <= 0) return;
                _replicas[r].Search(
                    new ReadOnlySpan<float>((float*)qp + (long)start * d, count * d), count, k,
                    new Span<float>((float*)dp + (long)start * k, count * k),
                    new Span<long>((long*)lp + (long)start * k, count * k));
            });
        }
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        _replicas.Count > 0
            ? _replicas[0].RangeSearch(queries, radius)
            : throw new InvalidOperationException("IndexReplicas has no replicas.");

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        long removed = 0;
        foreach (var replica in _replicas) removed = replica.RemoveIds(ids);
        Ntotal = _replicas.Count > 0 ? _replicas[0].Ntotal : 0;
        return removed;
    }

    public override void Reset()
    {
        foreach (var replica in _replicas) replica.Reset();
        Ntotal = 0;
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        if (_replicas.Count == 0) throw new InvalidOperationException("IndexReplicas has no replicas.");
        _replicas[0].Reconstruct(key, output);
    }

    public override long MemoryUsage => _replicas.Sum(r => r.MemoryUsage);

    public override string Describe() =>
        $"IndexReplicas({_replicas.Count} x {(_replicas.Count > 0 ? _replicas[0].Describe() : "empty")})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.Replicas;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(_replicas.Count);
        foreach (var replica in _replicas) IndexIO.WriteTo(writer, replica);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        _replicas.Clear();
        for (int i = 0; i < count; i++) _replicas.Add(IndexIO.ReadFrom(reader));
        Ntotal = _replicas.Count > 0 ? _replicas[0].Ntotal : 0;
        IsTrained = _replicas.TrueForAll(r => r.IsTrained);
    }
}

/// <summary>
/// Splits the data across sub-indexes and merges their results.
/// <para>
/// Where <see cref="IndexReplicas"/> scales throughput, this scales capacity: each shard holds a
/// slice of the database, every query goes to every shard, and the per-shard top-k lists are merged
/// into one. Ids are made globally unique by offsetting each shard's local ids, so the caller sees a
/// single flat id space.
/// </para>
/// </summary>
public sealed class IndexShards : Index
{
    private readonly List<Index> _shards = [];
    private readonly List<long> _idOffsets = [];
    private int _next;

    /// <summary>The shards, in the order they were added.</summary>
    public IReadOnlyList<Index> Shards => _shards;

    /// <summary>When true, <see cref="Add"/> sends the whole batch to one shard, round-robin.</summary>
    public bool RoundRobinAdds { get; set; } = true;

    public IndexShards(int dimension, MetricType metric = MetricType.L2) : base(dimension, metric) { }

    /// <summary>Adds a shard. Its ids are offset by the total count of the preceding shards.</summary>
    public void AddShard(Index index)
    {
        if (index.D != D) throw new ArgumentException($"Shard dimension {index.D} does not match {D}.");
        _idOffsets.Add(_shards.Count == 0 ? 0 : _idOffsets[^1] + _shards[^1].Ntotal);
        _shards.Add(index);
        IsTrained = _shards.TrueForAll(s => s.IsTrained);
    }

    public override void Train(ReadOnlySpan<float> x)
    {
        foreach (var shard in _shards) shard.Train(x);
        IsTrained = true;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        if (_shards.Count == 0) throw new InvalidOperationException("IndexShards has no shards.");
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;

        if (RoundRobinAdds)
        {
            _shards[_next].Add(x);
            _next = (_next + 1) % _shards.Count;
        }
        else
        {
            int chunk = (n + _shards.Count - 1) / _shards.Count;
            for (int s = 0; s < _shards.Count; s++)
            {
                int start = s * chunk;
                int count = Math.Min(chunk, n - start);
                if (count > 0) _shards[s].Add(x.Slice(start * D, count * D));
            }
        }

        RecomputeOffsets();
    }

    private void RecomputeOffsets()
    {
        long offset = 0;
        for (int s = 0; s < _shards.Count; s++)
        {
            _idOffsets[s] = offset;
            offset += _shards[s].Ntotal;
        }
        Ntotal = offset;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (_shards.Count == 0) throw new InvalidOperationException("IndexShards has no shards.");

        var partials = new SearchResult[_shards.Count];
        for (int s = 0; s < _shards.Count; s++)
        {
            partials[s] = SearchResult.Allocate(nq, k);
            _shards[s].Search(queries, nq, k, partials[s].Distances, partials[s].Labels);
        }

        if (MetricType.IsSimilarity()) Merge<DescendingOrder>(partials, nq, k, distances, labels);
        else Merge<AscendingOrder>(partials, nq, k, distances, labels);
    }

    /// <summary>Merges per-shard top-k lists, rebasing each shard's local ids into the global space.</summary>
    private unsafe void Merge<TOrder>(SearchResult[] partials, int nq, int k, Span<float> distances, Span<long> labels)
        where TOrder : struct, IScoreOrder
    {
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            for (int q = 0; q < nq; q++)
            {
                var heap = new KnnHeap<TOrder>(
                    new Span<float>(pdis + (long)q * k, k),
                    new Span<long>(plab + (long)q * k, k));

                for (int s = 0; s < partials.Length; s++)
                {
                    var shardLabels = partials[s].LabelsFor(q);
                    var shardDistances = partials[s].DistancesFor(q);
                    for (int i = 0; i < k; i++)
                    {
                        if (shardLabels[i] < 0) break; // results are best-first, so -1 ends the list
                        heap.Push(shardDistances[i], shardLabels[i] + _idOffsets[s]);
                    }
                }
                heap.Finish();
            }
        }
    }

    public override void Reset()
    {
        foreach (var shard in _shards) shard.Reset();
        RecomputeOffsets();
        _next = 0;
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        for (int s = _shards.Count - 1; s >= 0; s--)
        {
            if (key < _idOffsets[s]) continue;
            _shards[s].Reconstruct(key - _idOffsets[s], output);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(key));
    }

    public override long MemoryUsage => _shards.Sum(s => s.MemoryUsage);

    public override string Describe() => $"IndexShards({_shards.Count} shards, ntotal={Ntotal})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.Shards;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(_shards.Count);
        foreach (var shard in _shards) IndexIO.WriteTo(writer, shard);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        _shards.Clear();
        _idOffsets.Clear();
        for (int i = 0; i < count; i++)
        {
            _idOffsets.Add(0);
            _shards.Add(IndexIO.ReadFrom(reader));
        }
        RecomputeOffsets();
        IsTrained = _shards.TrueForAll(s => s.IsTrained);
    }
}
