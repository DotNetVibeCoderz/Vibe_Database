using System.Runtime.InteropServices;
using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Hierarchical Navigable Small World graph over full-precision vectors — the fastest index here at
/// high recall, and the usual choice when queries must be answered in well under a millisecond.
/// <para>
/// Search is a greedy walk: descend the sparse upper layers to land near the query, then explore
/// layer 0 with a priority queue of width <see cref="IndexHNSW.EfSearch"/>. Cost grows
/// logarithmically with the database rather than linearly, and unlike IVF there is no training step
/// and no partition to go stale as data is added.
/// </para>
/// <para>
/// The trade is memory and build time: the graph adds roughly <c>4 * (2M + M * levels)</c> bytes per
/// vector on top of the vectors themselves, and construction runs a full search per insertion.
/// <see cref="IndexHNSW.M"/> and <see cref="IndexHNSW.EfConstruction"/> are set once at build time;
/// only <see cref="IndexHNSW.EfSearch"/> can be retuned afterwards. When the vectors themselves are
/// the memory problem, <see cref="IndexHNSWSQ"/> and <see cref="IndexHNSWPQ"/> keep the same graph
/// over compressed storage.
/// </para>
/// </summary>
public sealed class IndexHNSWFlat : IndexHNSW
{
    private readonly VectorStore _store;

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="m">Links per node above layer 0.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexHNSWFlat(int dimension, int m = 32, MetricType metric = MetricType.L2)
        : base(dimension, m, metric)
    {
        _store = new VectorStore(dimension);
        IsTrained = true;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Raw stored vectors, <c>ntotal * d</c> row-major.</summary>
    public ReadOnlySpan<float> Vectors => _store.AsSpan();

    private protected override void StorageAdd(ReadOnlySpan<float> x) => _store.Add(x);

    private protected override int StorageCount => _store.Count;

    private protected override HnswSession OpenSession() => new FlatSession(_store, D, MetricType);

    private protected override void StorageReset() => _store.Clear();

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _store.Count) throw new ArgumentOutOfRangeException(nameof(key));
        _store[(int)key].CopyTo(output);
    }

    public override unsafe void ComputeDistanceSubset(ReadOnlySpan<float> query, ReadOnlySpan<long> ids, Span<float> distances)
    {
        if (query.Length != D) throw new ArgumentException($"Expected a query of length {D}.", nameof(query));
        var (data, n) = _store.Snapshot();
        float worst = MetricType.IsSimilarity() ? float.MinValue : float.MaxValue;
        fixed (float* pq = query)
        fixed (float* xb = data)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                long id = ids[i];
                distances[i] = id >= 0 && id < n ? VectorOps.Distance(pq, xb + id * D, D, MetricType) : worst;
            }
        }
    }

    public override long MemoryUsage => _store.MemoryUsage + _graph.MemoryUsage;

    public override string Describe() =>
        $"IndexHNSWFlat(d={D}, ntotal={Ntotal}, M={M}, efConstruction={EfConstruction}, efSearch={EfSearch}, " +
        $"{MetricType.ToShortString()}, levels={_graph.MaxLevel + 1})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.HNSWFlat;

    private protected override void WriteStorage(BinaryWriter writer)
    {
        var (data, count) = _store.Snapshot();
        writer.Write(count);
        writer.Write(MemoryMarshal.AsBytes(data.AsSpan(0, count * D)));
    }

    private protected override void ReadStorage(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var buffer = new float[(long)count * D];
        reader.ReadExactly(MemoryMarshal.AsBytes(buffer.AsSpan()));
        _store.Clear();
        _store.Add(buffer);
    }

    // ----------------------------------------------------------- Distances

    /// <summary>Raw-float distances; inner product negated so smaller is better.</summary>
    private unsafe struct Computer(float* data, int d, MetricType metric, float* query) : IHnswComputer
    {
        public readonly float Query(int node) => Score(query, data + (long)node * d);

        public readonly float Pair(int a, int b) => Score(data + (long)a * d, data + (long)b * d);

        private readonly float Score(float* x, float* y) =>
            metric == MetricType.InnerProduct ? -VectorOps.InnerProduct(x, y, d) : VectorOps.Distance(x, y, d, metric);
    }

    private sealed unsafe class FlatSession : HnswSession<Computer>
    {
        private GCHandle _pin;
        private readonly float* _data;
        private readonly int _d;
        private readonly MetricType _metric;

        public FlatSession(VectorStore store, int d, MetricType metric)
        {
            var (data, count) = store.Snapshot();
            _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            _data = (float*)_pin.AddrOfPinnedObject();
            _d = d;
            _metric = metric;
            Limit = count;
        }

        protected override Computer ForQuery(nint query, HnswScratch scratch) => new(_data, _d, _metric, (float*)query);

        protected override Computer ForNode(int node, HnswScratch scratch) => new(_data, _d, _metric, _data + (long)node * _d);

        public override void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }
    }
}
