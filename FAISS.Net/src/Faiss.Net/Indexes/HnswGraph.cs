using Faiss.Net.Utils;

namespace Faiss.Net.Core;

/// <summary>
/// The layered proximity graph behind <see cref="IndexHNSWFlat"/>: a navigable small-world graph
/// where each node also appears in a geometrically thinning stack of upper layers.
/// <para>
/// Links live in one flat <c>int[]</c> with a per-node offset rather than an array per node. With
/// millions of nodes the difference is decisive: one allocation instead of millions, neighbours of a
/// node contiguous in memory, and the GC never walking the graph.
/// </para>
/// <para>
/// Slot layout per node: <c>M0</c> entries for layer 0, then <c>M</c> entries for each higher layer
/// the node reaches. Layer 0 gets double the degree because it holds every node and carries the
/// final, accuracy-determining hop.
/// </para>
/// </summary>
public sealed class HnswGraph
{
    /// <summary>Empty link slot marker.</summary>
    public const int NoNeighbor = -1;

    private int[] _neighbors = [];
    private long[] _offsets = [0];
    private int[] _levels = [];
    private object[] _locks = [];
    private int _count;

    /// <summary>Links per node above layer 0.</summary>
    public int M { get; }

    /// <summary>Links per node on layer 0, <c>2 * M</c>.</summary>
    public int M0 => M * 2;

    /// <summary>Highest layer currently occupied.</summary>
    public int MaxLevel { get; private set; } = -1;

    /// <summary>Node the descent starts from; the sole node on <see cref="MaxLevel"/>.</summary>
    public int EntryPoint { get; private set; } = -1;

    /// <summary>Nodes in the graph.</summary>
    public int Count => _count;

    /// <summary>Level assignment multiplier, <c>1 / ln(M)</c>, giving the geometric layer decay.</summary>
    public double LevelMultiplier { get; }

    public HnswGraph(int m)
    {
        if (m < 2) throw new ArgumentOutOfRangeException(nameof(m), "M must be at least 2.");
        M = m;
        LevelMultiplier = 1.0 / Math.Log(m);
    }

    /// <summary>Top layer reached by a node.</summary>
    public int LevelOf(int node) => _levels[node];

    /// <summary>Slots occupied by a node across all its layers.</summary>
    private int SlotsFor(int level) => M0 + level * M;

    /// <summary>Draws a level from the exponential distribution HNSW prescribes.</summary>
    public int RandomLevel(RandomGenerator rng)
    {
        double u = Math.Max(rng.NextFloat(), 1e-7f);
        return (int)(-Math.Log(u) * LevelMultiplier);
    }

    /// <summary>
    /// Appends nodes with pre-drawn levels and reserves their link slots up front, so the parallel
    /// linking phase never resizes shared arrays.
    /// </summary>
    public void AddNodes(ReadOnlySpan<int> levels)
    {
        int added = levels.Length;
        int newCount = _count + added;

        Array.Resize(ref _levels, newCount);
        Array.Resize(ref _offsets, newCount + 1);
        Array.Resize(ref _locks, newCount);

        long slots = _offsets[_count];
        for (int i = 0; i < added; i++)
        {
            int node = _count + i;
            _levels[node] = levels[i];
            _offsets[node] = slots;
            slots += SlotsFor(levels[i]);
            _offsets[node + 1] = slots;
            _locks[node] = new object();
        }

        long previousLength = _neighbors.Length;
        if (slots > _neighbors.Length)
        {
            Array.Resize(ref _neighbors, (int)slots);
            _neighbors.AsSpan((int)previousLength).Fill(NoNeighbor);
        }

        _count = newCount;
    }

    /// <summary>Neighbour slots of one node on one layer. Unused slots hold <see cref="NoNeighbor"/>.</summary>
    public Span<int> Neighbors(int node, int level)
    {
        long start = _offsets[node] + (level == 0 ? 0 : M0 + (long)(level - 1) * M);
        int size = level == 0 ? M0 : M;
        return _neighbors.AsSpan((int)start, size);
    }

    /// <summary>Lock guarding mutations of one node's links during concurrent construction.</summary>
    public object LockFor(int node) => _locks[node];

    /// <summary>Promotes a node to be the entry point. Callers must serialize this.</summary>
    public void SetEntryPoint(int node, int level)
    {
        EntryPoint = node;
        MaxLevel = level;
    }

    /// <summary>Drops every node and link.</summary>
    public void Reset()
    {
        _count = 0;
        _offsets = [0];
        _levels = [];
        _locks = [];
        _neighbors = [];
        EntryPoint = -1;
        MaxLevel = -1;
    }

    /// <summary>Approximate resident bytes.</summary>
    public long MemoryUsage =>
        (long)_neighbors.Length * sizeof(int) +
        (long)_offsets.Length * sizeof(long) +
        (long)_levels.Length * sizeof(int) +
        (long)_locks.Length * 8;

    /// <summary>Nodes present on each layer, layer 0 first. Useful for diagnosing a badly shaped graph.</summary>
    public int[] LayerSizes()
    {
        var sizes = new int[Math.Max(1, MaxLevel + 1)];
        for (int node = 0; node < _count; node++)
            for (int level = 0; level <= _levels[node] && level < sizes.Length; level++)
                sizes[level]++;
        return sizes;
    }

    /// <summary>Mean out-degree on layer 0; well below <see cref="M0"/> means the graph is sparse and recall will suffer.</summary>
    public double AverageDegree()
    {
        if (_count == 0) return 0;
        long links = 0;
        for (int node = 0; node < _count; node++)
            foreach (int neighbor in Neighbors(node, 0))
                if (neighbor != NoNeighbor) links++;
        return links / (double)_count;
    }

    // -------------------------------------------------------- Serialization

    public void Write(BinaryWriter writer)
    {
        writer.Write(M);
        writer.Write(_count);
        writer.Write(MaxLevel);
        writer.Write(EntryPoint);
        for (int i = 0; i < _count; i++) writer.Write(_levels[i]);
        writer.Write(_offsets[_count]);
        for (long i = 0; i < _offsets[_count]; i++) writer.Write(_neighbors[i]);
    }

    public static HnswGraph Read(BinaryReader reader)
    {
        var graph = new HnswGraph(reader.ReadInt32());
        int count = reader.ReadInt32();
        int maxLevel = reader.ReadInt32();
        int entryPoint = reader.ReadInt32();

        var levels = new int[count];
        for (int i = 0; i < count; i++) levels[i] = reader.ReadInt32();
        graph.AddNodes(levels);

        long slots = reader.ReadInt64();
        for (long i = 0; i < slots; i++) graph._neighbors[i] = reader.ReadInt32();

        graph.EntryPoint = entryPoint;
        graph.MaxLevel = maxLevel;
        return graph;
    }
}
