namespace Faiss.Net.Utils;

/// <summary>
/// Constant-time "have I seen this node?" set for graph traversal.
/// <para>
/// A graph search visits a few hundred nodes out of millions, so clearing a bitmap between queries
/// would dominate the query itself. Instead each entry stores the id of the traversal that last
/// touched it and clearing is a single increment — O(1) reset regardless of graph size. The array is
/// only ever wiped on the rare counter wraparound.
/// </para>
/// </summary>
public sealed class VisitedTable
{
    private int[] _stamps;
    private int _version;

    public VisitedTable(int capacity = 0)
    {
        _stamps = capacity > 0 ? new int[capacity] : [];
    }

    /// <summary>Starts a new traversal, growing the table if the graph has since grown.</summary>
    public void Reset(int capacity)
    {
        if (_stamps.Length < capacity)
        {
            Array.Resize(ref _stamps, Math.Max(capacity, _stamps.Length * 2));
            _version = 0;
        }

        if (_version == int.MaxValue)
        {
            Array.Clear(_stamps);
            _version = 0;
        }
        _version++;
    }

    /// <summary>Marks a node visited; returns true the first time it is seen in this traversal.</summary>
    public bool Visit(int node)
    {
        if (_stamps[node] == _version) return false;
        _stamps[node] = _version;
        return true;
    }

    /// <summary>True when the node has already been seen in this traversal.</summary>
    public bool WasVisited(int node) => _stamps[node] == _version;
}
