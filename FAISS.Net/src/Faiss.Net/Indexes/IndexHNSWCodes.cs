using System.Runtime.InteropServices;
using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// HNSW graph over scalar-quantized storage: the speed of <see cref="IndexHNSWFlat"/> at a quarter
/// of the vector memory (8-bit) or half (fp16). The graph itself is unchanged, so on 128-dim data
/// with <c>M = 32</c> the total drops from about 0.8 KB per vector to about 0.4 KB.
/// <para>
/// Distances are computed against decoded codes, both during construction and at search time, so
/// recall tracks the quantizer's own accuracy — usually within a point of the flat graph at 8 bits.
/// Needs a training pass (the min/max of each dimension) before the first add.
/// </para>
/// </summary>
public sealed class IndexHNSWSQ : IndexHNSW
{
    private CodeStore _codes;

    /// <summary>The scalar quantizer; exposed like <c>index.storage.sq</c> in Python.</summary>
    public ScalarQuantizer Sq { get; private set; }

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="type">Scalar quantizer type.</param>
    /// <param name="m">Links per node above layer 0.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexHNSWSQ(int dimension, ScalarQuantizerType type = ScalarQuantizerType.PerDimension8Bit, int m = 32,
        MetricType metric = MetricType.L2)
        : base(dimension, m, metric)
    {
        Sq = new ScalarQuantizer(dimension, type);
        _codes = new CodeStore(Sq.CodeSize);
        IsTrained = Sq.IsTrained;
    }

    public override bool SupportsReconstruct => true;

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Sq.Train(x);
        IsTrained = true;
    }

    private protected override unsafe void StorageAdd(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        fixed (float* px = x)
        {
            nint xp = (nint)px;
            _codes.Append(n, codes => Sq.EncodeBatch(new ReadOnlySpan<float>((float*)xp, n * D), n, codes));
        }
    }

    private protected override int StorageCount => _codes.Count;

    private protected override HnswSession OpenSession() => new SqSession(this);

    private protected override void StorageReset() => _codes.Clear();

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        Sq.Decode(_codes[(int)key], output);
    }

    public override long MemoryUsage => _codes.MemoryUsage + _graph.MemoryUsage;

    public override string Describe() =>
        $"IndexHNSWSQ(d={D}, ntotal={Ntotal}, M={M}, {Sq}, efSearch={EfSearch}, {MetricType.ToShortString()})";

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.HNSWSQ;

    private protected override void WriteStorage(BinaryWriter writer)
    {
        Sq.Write(writer);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * Sq.CodeSize));
    }

    private protected override void ReadStorage(BinaryReader reader)
    {
        Sq = ScalarQuantizer.Read(reader);
        int count = reader.ReadInt32();
        var data = new byte[(long)count * Sq.CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(Sq.CodeSize);
        _codes.Load(data, count);
        IsTrained = Sq.IsTrained;
    }

    /// <summary>Decode-then-compare distances; inner product negated so smaller is better.</summary>
    private unsafe struct Computer(
        ScalarQuantizer sq, byte* codes, int codeSize, float* offsets, float* steps,
        float* query, float* a, float* b, int d, MetricType metric) : IHnswComputer
    {
        public readonly float Query(int node)
        {
            sq.DecodeUnchecked(codes + (long)node * codeSize, a, offsets, steps);
            return Score(query, a);
        }

        public readonly float Pair(int x, int y)
        {
            sq.DecodeUnchecked(codes + (long)x * codeSize, a, offsets, steps);
            sq.DecodeUnchecked(codes + (long)y * codeSize, b, offsets, steps);
            return Score(a, b);
        }

        private readonly float Score(float* x, float* y) =>
            metric == MetricType.InnerProduct ? -VectorOps.InnerProduct(x, y, d) : VectorOps.Distance(x, y, d, metric);
    }

    private sealed unsafe class SqSession : HnswSession<Computer>
    {
        private GCHandle _codesPin, _offsetsPin, _stepsPin;
        private readonly ScalarQuantizer _sq;
        private readonly byte* _codes;
        private readonly float* _offsets, _steps;
        private readonly int _d;
        private readonly MetricType _metric;

        public SqSession(IndexHNSWSQ index)
        {
            var (data, count) = index._codes.Snapshot();
            _sq = index.Sq;
            _codesPin = GCHandle.Alloc(data, GCHandleType.Pinned);
            _offsetsPin = GCHandle.Alloc(_sq.Offsets, GCHandleType.Pinned);
            _stepsPin = GCHandle.Alloc(_sq.Steps, GCHandleType.Pinned);
            _codes = (byte*)_codesPin.AddrOfPinnedObject();
            _offsets = (float*)_offsetsPin.AddrOfPinnedObject();
            _steps = (float*)_stepsPin.AddrOfPinnedObject();
            _d = index.D;
            _metric = index.MetricType;
            Limit = count;
        }

        protected override Computer ForQuery(nint query, HnswScratch scratch) =>
            new(_sq, _codes, _sq.CodeSize, _offsets, _steps, (float*)query, scratch.A(_d), scratch.B(_d), _d, _metric);

        protected override Computer ForNode(int node, HnswScratch scratch)
        {
            float* q = scratch.Q(_d);
            _sq.DecodeUnchecked(_codes + (long)node * _sq.CodeSize, q, _offsets, _steps);
            return new(_sq, _codes, _sq.CodeSize, _offsets, _steps, q, scratch.A(_d), scratch.B(_d), _d, _metric);
        }

        public override void Dispose()
        {
            if (_codesPin.IsAllocated) _codesPin.Free();
            if (_offsetsPin.IsAllocated) _offsetsPin.Free();
            if (_stepsPin.IsAllocated) _stepsPin.Free();
        }
    }
}

/// <summary>
/// HNSW graph over product-quantized storage — the smallest graph index: with <c>pqM = 16</c> on
/// 128-dim data a vector costs 16 bytes plus its links, against 512 for <see cref="IndexHNSWFlat"/>.
/// <para>
/// Searches score codes through a per-query ADC table, exactly as <see cref="IndexPQ"/> does, so the
/// walk itself is cheap; construction compares decoded vectors. Recall is bounded by PQ accuracy,
/// which is the usual reason to pair this with <see cref="IndexRefine"/> and a flat re-ranker.
/// </para>
/// </summary>
public sealed class IndexHNSWPQ : IndexHNSW
{
    private CodeStore _codes;

    /// <summary>The product quantizer; exposed like <c>index.storage.pq</c> in Python.</summary>
    public ProductQuantizer Pq { get; private set; }

    /// <param name="dimension">Vector dimension.</param>
    /// <param name="pqM">PQ sub-quantizers; must divide <paramref name="dimension"/>.</param>
    /// <param name="m">Links per node above layer 0.</param>
    /// <param name="pqNbits">Bits per PQ sub-code.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexHNSWPQ(int dimension, int pqM, int m = 32, int pqNbits = 8, MetricType metric = MetricType.L2)
        : base(dimension, m, metric)
    {
        Pq = new ProductQuantizer(dimension, pqM, pqNbits);
        _codes = new CodeStore(Pq.CodeSize);
        IsTrained = false;
    }

    public override bool SupportsReconstruct => true;

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Pq.Train(x);
        IsTrained = true;
    }

    private protected override unsafe void StorageAdd(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        fixed (float* px = x)
        {
            nint xp = (nint)px;
            _codes.Append(n, codes => Pq.ComputeCodes(new ReadOnlySpan<float>((float*)xp, n * D), n, codes));
        }
    }

    private protected override int StorageCount => _codes.Count;

    private protected override HnswSession OpenSession() => new PqSession(this);

    private protected override void StorageReset() => _codes.Clear();

    public override void Reconstruct(long key, Span<float> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        Pq.Decode(_codes[(int)key], output);
    }

    public override long MemoryUsage => _codes.MemoryUsage + _graph.MemoryUsage + Pq.CodebookBytes;

    public override string Describe() =>
        $"IndexHNSWPQ(d={D}, ntotal={Ntotal}, M={M}, {Pq}, efSearch={EfSearch}, {MetricType.ToShortString()})";

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.HNSWPQ;

    private protected override void WriteStorage(BinaryWriter writer)
    {
        Pq.Write(writer);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * Pq.CodeSize));
    }

    private protected override void ReadStorage(BinaryReader reader)
    {
        Pq = ProductQuantizer.Read(reader);
        int count = reader.ReadInt32();
        var data = new byte[(long)count * Pq.CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(Pq.CodeSize);
        _codes.Load(data, count);
        IsTrained = Pq.IsTrained;
    }

    /// <summary>ADC lookups against the query's table; decoded vectors for node-to-node scores.</summary>
    private unsafe struct Computer(
        ProductQuantizer pq, byte* codes, float* table, float* a, float* b, int d, MetricType metric) : IHnswComputer
    {
        public readonly float Query(int node)
        {
            float value = pq.DistanceFromTable(table, codes + (long)node * pq.CodeSize);
            return metric == MetricType.InnerProduct ? -value : value;
        }

        public readonly float Pair(int x, int y)
        {
            pq.Decode(new ReadOnlySpan<byte>(codes + (long)x * pq.CodeSize, pq.CodeSize), new Span<float>(a, d));
            pq.Decode(new ReadOnlySpan<byte>(codes + (long)y * pq.CodeSize, pq.CodeSize), new Span<float>(b, d));
            return metric == MetricType.InnerProduct ? -VectorOps.InnerProduct(a, b, d) : VectorOps.Distance(a, b, d, metric);
        }
    }

    private sealed unsafe class PqSession : HnswSession<Computer>
    {
        private GCHandle _codesPin;
        private readonly ProductQuantizer _pq;
        private readonly byte* _codes;
        private readonly int _d;
        private readonly MetricType _metric;

        public PqSession(IndexHNSWPQ index)
        {
            var (data, count) = index._codes.Snapshot();
            _pq = index.Pq;
            _codesPin = GCHandle.Alloc(data, GCHandleType.Pinned);
            _codes = (byte*)_codesPin.AddrOfPinnedObject();
            _d = index.D;
            _metric = index.MetricType;
            Limit = count;
        }

        protected override Computer ForQuery(nint query, HnswScratch scratch) => Build((float*)query, scratch);

        protected override Computer ForNode(int node, HnswScratch scratch)
        {
            float* q = scratch.Q(_d);
            _pq.Decode(new ReadOnlySpan<byte>(_codes + (long)node * _pq.CodeSize, _pq.CodeSize), new Span<float>(q, _d));
            return Build(q, scratch);
        }

        private Computer Build(float* query, HnswScratch scratch)
        {
            int size = _pq.DistanceTableSize;
            float* table = scratch.Table(size);
            _pq.ComputeDistanceTable(new ReadOnlySpan<float>(query, _d), new Span<float>(table, size),
                _metric == MetricType.InnerProduct ? MetricType.InnerProduct : MetricType.L2);
            return new(_pq, _codes, table, scratch.A(_d), scratch.B(_d), _d, _metric);
        }

        public override void Dispose()
        {
            if (_codesPin.IsAllocated) _codesPin.Free();
        }
    }
}
