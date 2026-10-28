using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net.Binary;

/// <summary>
/// HNSW graph over binary codes under Hamming distance — the FAISS <c>IndexBinaryHNSW</c>.
/// <para>
/// A Hamming distance is a handful of XOR and popcount instructions, so each hop of the walk is
/// cheaper than for floats, and the graph reaches high recall on 256-bit codes visiting a few
/// hundred codes where <see cref="IndexBinaryFlat"/> visits all of them. It shares its graph
/// algorithms with <see cref="IndexHNSWFlat"/> — the same engine, specialized for a Hamming
/// distance computer — including the level-0 sink repair of
/// <see cref="AddLevel0LinksFromSample"/>.
/// </para>
/// <para>
/// Hamming distances are small integers, so ties are common: with an <see cref="EfSearch"/> below
/// the number of codes at the k-th distance, which of the tied codes is returned depends on the
/// walk. Searches may run while one writer adds, as for the float HNSW family.
/// </para>
/// </summary>
public sealed class IndexBinaryHNSW : IndexBinary
{
    private CodeStore _codes;
    private HnswGraph _graph;
    private readonly RandomGenerator _levelRng = new(1234);
    private readonly object _entryPointLock = new();
    private readonly ConcurrentBag<HnswScratch> _scratchPool = [];

    /// <summary>Links per node above layer 0.</summary>
    public int M => _graph.M;

    /// <summary>Search width during construction.</summary>
    public int EfConstruction { get; set; } = 40;

    /// <summary>Search width at query time; overridable per call through <see cref="SearchParametersHNSW.EfSearch"/>.</summary>
    public int EfSearch { get; set; } = 16;

    /// <summary>The underlying graph.</summary>
    public HnswGraph Graph => _graph;

    /// <param name="dimension">Code length in bits; a multiple of 8.</param>
    /// <param name="m">Links per node above layer 0.</param>
    public IndexBinaryHNSW(int dimension, int m = 32) : base(dimension)
    {
        _codes = new CodeStore(CodeSize);
        _graph = new HnswGraph(m);
    }

    /// <summary>Stored codes, <c>ntotal * CodeSize</c> bytes.</summary>
    public ReadOnlySpan<byte> Codes => _codes.AsSpan();

    public override void Add(ReadOnlySpan<byte> x)
    {
        if (x.Length % CodeSize != 0)
            throw new ArgumentException($"Input length {x.Length} is not a multiple of the code size {CodeSize}.", nameof(x));
        int n = x.Length / CodeSize;
        if (n == 0) return;

        int first = _codes.Count;
        var copy = x.ToArray();
        _codes.Append(n, codes => copy.CopyTo(codes));

        var levels = new int[n];
        for (int i = 0; i < n; i++) levels[i] = _graph.RandomLevel(_levelRng);
        _graph.AddNodes(levels);

        using (var session = new HammingSession(this))
            HnswBuild.InsertAll(_graph, session, first, levels, EfConstruction,
                Threads > 0 ? Threads : Environment.ProcessorCount, _entryPointLock, Rent, Release);

        Ntotal = _codes.Count;
    }

    public override void Search(ReadOnlySpan<byte> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override unsafe void Search(
        ReadOnlySpan<byte> queries, int nq, int k, Span<float> distances, Span<long> labels, SearchParameters? parameters)
    {
        if (nq == 0) return;
        var (entry, maxLevel) = _graph.Entry;
        if (entry < 0)
        {
            distances[..(nq * k)].Fill(float.MaxValue);
            labels[..(nq * k)].Fill(-1);
            return;
        }

        int ef = Math.Max(parameters is SearchParametersHNSW { EfSearch: > 0 } p ? p.EfSearch : EfSearch, k);
        var sel = parameters?.Sel;
        int codeSize = CodeSize;

        using var session = new HammingSession(this);
        fixed (byte* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int threads = Threads > 0 ? Threads : Environment.ProcessorCount;

            void One(int q)
            {
                var scratch = Rent();
                try
                {
                    var found = session.Search(_graph, qp + (nint)q * codeSize, entry, maxLevel, ef, scratch, sel);
                    float* outD = (float*)dp + (long)q * k;
                    long* outL = (long*)lp + (long)q * k;
                    for (int i = 0; i < k; i++)
                    {
                        bool has = i < found.Count;
                        outD[i] = has ? found[i].Score : float.MaxValue;
                        outL[i] = has ? found[i].Node : -1;
                    }
                }
                finally
                {
                    Release(scratch);
                }
            }

            if (nq == 1 || threads == 1) for (int q = 0; q < nq; q++) One(q);
            else Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = threads }, One);
        }
    }

    /// <summary>Approximate radius search: a wide beam, keeping everything within the radius.</summary>
    public override unsafe RangeSearchResult RangeSearch(ReadOnlySpan<byte> queries, int radius)
    {
        int nq = queries.Length / CodeSize;
        var (entry, maxLevel) = _graph.Entry;
        if (nq == 0 || entry < 0) return new RangeSearchResult(new long[nq + 1], [], []);

        var perQuery = new List<(long Id, float Distance)>[nq];
        int ef = Math.Max(EfSearch, 64), codeSize = CodeSize;
        using var session = new HammingSession(this);
        fixed (byte* xq = queries)
        {
            nint qp = (nint)xq;
            Parallel.For(0, nq, q =>
            {
                var scratch = Rent();
                try
                {
                    var hits = new List<(long, float)>();
                    foreach (var (node, score) in session.Search(_graph, qp + (nint)q * codeSize, entry, maxLevel, ef, scratch, null))
                        if (score <= radius) hits.Add((node, score));
                    perQuery[q] = hits;
                }
                finally
                {
                    Release(scratch);
                }
            });
        }
        return BruteForce.Flatten(perQuery);
    }

    /// <summary>
    /// Sink repair for the binary graph, as for <see cref="IndexHNSW.AddLevel0LinksFromSample"/>.
    /// Hamming distances are integers, so a node counts as nearer only when strictly nearer.
    /// </summary>
    public unsafe HNSWLevel0LinkStats AddLevel0LinksFromSample(ReadOnlySpan<byte> x, HNSWLevel0LinkParameters? parameters = null)
    {
        int n = x.Length / CodeSize;
        var p = parameters ?? new HNSWLevel0LinkParameters();
        var stats = new HNSWLevel0LinkStats();
        if (n == 0 || _graph.EntryPoint < 0) return stats;

        // No relative tolerance on integer distances: any strictly smaller one is genuinely nearer.
        var exact = new HNSWLevel0LinkParameters
        {
            MinMeanFactor = p.MinMeanFactor, MinCloserFraction = p.MinCloserFraction,
            MaxLinksPerSink = p.MaxLinksPerSink, MaxRounds = p.MaxRounds, RelativeTolerance = 0, SearchParams = p.SearchParams,
        };
        int ef = Math.Max(1, p.SearchParams is { EfSearch: > 0 } s ? s.EfSearch : EfSearch);
        fixed (byte* px = x)
        {
            using var session = new HammingSession(this);
            new HnswSinkRepair(_graph, session, (nint)px, n, CodeSize, ef, exact, Rent, Release,
                Threads > 0 ? Threads : Environment.ProcessorCount).Run(stats);
        }
        return stats;
    }

    /// <summary>Copies one stored code.</summary>
    public void Reconstruct(long key, Span<byte> output)
    {
        if (key < 0 || key >= _codes.Count) throw new ArgumentOutOfRangeException(nameof(key));
        _codes[(int)key].CopyTo(output);
    }

    public override void Reset()
    {
        _codes.Clear();
        _graph = new HnswGraph(M);
        Ntotal = 0;
    }

    public override long MemoryUsage => _codes.MemoryUsage + _graph.MemoryUsage;

    public override string Describe() =>
        $"IndexBinaryHNSW(d={D} bits, ntotal={Ntotal}, M={M}, efSearch={EfSearch})";

    private HnswScratch Rent() => _scratchPool.TryTake(out var scratch) ? scratch : new HnswScratch();

    private void Release(HnswScratch scratch) => _scratchPool.Add(scratch);

    // -------------------------------------------------------- Serialization

    protected internal override int SerializationParameter => M;

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.BinaryHNSW;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(EfConstruction);
        writer.Write(EfSearch);
        var (data, count) = _codes.Snapshot();
        writer.Write(count);
        writer.Write(data.AsSpan(0, count * CodeSize));
        _graph.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        EfConstruction = reader.ReadInt32();
        EfSearch = reader.ReadInt32();
        int count = reader.ReadInt32();
        var data = new byte[(long)count * CodeSize];
        reader.ReadExactly(data);
        _codes = new CodeStore(CodeSize);
        _codes.Load(data, count);
        _graph = HnswGraph.Read(reader);
        Ntotal = count;
    }

    // ----------------------------------------------------------- Distances

    private unsafe struct Computer(byte* codes, int codeSize, byte* query) : IHnswComputer
    {
        public readonly float Query(int node) => HammingOps.Distance(query, codes + (long)node * codeSize, codeSize);

        public readonly float Pair(int a, int b) =>
            HammingOps.Distance(codes + (long)a * codeSize, codes + (long)b * codeSize, codeSize);
    }

    private sealed unsafe class HammingSession : HnswSession<Computer>
    {
        private GCHandle _pin;
        private readonly byte* _codes;
        private readonly int _codeSize;

        public HammingSession(IndexBinaryHNSW index)
        {
            var (data, count) = index._codes.Snapshot();
            _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            _codes = (byte*)_pin.AddrOfPinnedObject();
            _codeSize = index.CodeSize;
            Limit = count;
        }

        protected override Computer ForQuery(nint query, HnswScratch scratch) => new(_codes, _codeSize, (byte*)query);

        protected override Computer ForNode(int node, HnswScratch scratch) => new(_codes, _codeSize, _codes + (long)node * _codeSize);

        public override void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }
    }
}
