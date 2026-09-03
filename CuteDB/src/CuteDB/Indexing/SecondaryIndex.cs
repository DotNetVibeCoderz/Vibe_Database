using CuteDB.Storage;

namespace CuteDB.Indexing;

/// <summary>Describes a secondary index, as stored in the log and reported to callers.</summary>
/// <param name="Name">The index name, unique within its collection.</param>
/// <param name="Path">The document path being indexed.</param>
/// <param name="Unique">Whether duplicate keys are rejected.</param>
public sealed record CuteIndexInfo(string Name, string Path, bool Unique)
{
    /// <summary>The number of distinct keys currently held.</summary>
    public int KeyCount { get; internal set; }

    /// <summary>The number of rows currently indexed.</summary>
    public int EntryCount { get; internal set; }
}

/// <summary>
/// A secondary index over one document path.
/// </summary>
/// <remarks>
/// <para>
/// The index keeps two views of the same data. A dictionary from key to row list answers equality
/// lookups in constant time, which is what the overwhelming majority of queries ask for. A sorted
/// array of keys answers range and prefix lookups, and is rebuilt lazily: writes only mark it
/// stale, so a bulk load of a million documents sorts once at the first range query rather than
/// re-sorting on every insert.
/// </para>
/// <para>
/// A document whose indexed path resolves to <see cref="CuteType.Missing"/> is not indexed at all.
/// That keeps sparse indexes cheap — indexing <c>discount.code</c> across a million orders where
/// only a few thousand carry one costs a few thousand entries — and it is why a unique index does
/// not treat two documents that both lack the field as a collision.
/// </para>
/// <para>
/// An array-valued key is indexed once per element, so an index on <c>tags</c> lets
/// <c>WHERE tags = 'sale'</c> find every document carrying that tag.
/// </para>
/// </remarks>
internal sealed class SecondaryIndex
{
    private readonly Dictionary<CuteValue, RowSet> _byKey;
    private CuteValue[] _sortedKeys = [];
    private bool _sortedKeysValid;

    internal SecondaryIndex(string name, CutePath path, bool unique)
    {
        Info = new CuteIndexInfo(name, path.Text, unique);
        Path = path;
        _byKey = new Dictionary<CuteValue, RowSet>(CuteValueEqualityComparer.Instance);
    }

    /// <summary>The index's public description.</summary>
    internal CuteIndexInfo Info { get; }

    /// <summary>The compiled path being indexed.</summary>
    internal CutePath Path { get; }

    /// <summary>The index name.</summary>
    internal string Name => Info.Name;

    /// <summary>Whether duplicate keys are rejected.</summary>
    internal bool Unique => Info.Unique;

    /// <summary>The number of distinct keys.</summary>
    internal int KeyCount => _byKey.Count;

    /// <summary>Extracts the key or keys a document contributes. Missing means "do not index".</summary>
    internal void ExtractKeys(ReadOnlySpan<byte> document, List<CuteValue> keys)
    {
        keys.Clear();
        var value = Path.ResolveEncoded(document);
        if (value.IsMissing)
        {
            return;
        }

        if (value.IsArray)
        {
            // Indexing each element is what makes tag-style queries work without a join.
            foreach (var element in value.AsArray.AsSpan())
            {
                if (!element.IsMissing)
                {
                    keys.Add(element);
                }
            }

            return;
        }

        keys.Add(value);
    }

    /// <summary>Records that <paramref name="row"/> carries <paramref name="key"/>.</summary>
    internal void Add(CuteValue key, int row)
    {
        ref var set = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_byKey, key, out var existed);
        if (!existed)
        {
            set = new RowSet(row);
            _sortedKeysValid = false;
        }
        else
        {
            if (Unique && set!.Count > 0)
            {
                throw new CuteDbException(
                    $"Unique index '{Name}' already has a document with {Path.Text} = {key.ToDisplayString()}.");
            }

            set!.Add(row);
        }

        Info.EntryCount++;
        Info.KeyCount = _byKey.Count;
    }

    /// <summary>Removes a row from a key's entry.</summary>
    internal void Remove(CuteValue key, int row)
    {
        if (!_byKey.TryGetValue(key, out var set))
        {
            return;
        }

        if (!set.Remove(row))
        {
            return;
        }

        Info.EntryCount--;
        if (set.Count == 0)
        {
            _byKey.Remove(key);
            _sortedKeysValid = false;
            Info.KeyCount = _byKey.Count;
        }
    }

    /// <summary>Drops every entry.</summary>
    internal void Clear()
    {
        _byKey.Clear();
        _sortedKeys = [];
        _sortedKeysValid = false;
        Info.EntryCount = 0;
        Info.KeyCount = 0;
    }

    /// <summary>Rows whose key equals <paramref name="key"/>. Empty when there are none.</summary>
    internal ReadOnlySpan<int> Equal(CuteValue key)
        => _byKey.TryGetValue(key, out var set) ? set.AsSpan() : ReadOnlySpan<int>.Empty;

    /// <summary>True when the index holds this key at all.</summary>
    internal bool ContainsKey(CuteValue key) => _byKey.ContainsKey(key);

    /// <summary>
    /// Rows whose key falls in a range. Either bound may be <see cref="CuteValue.Missing"/> to
    /// leave that side open.
    /// </summary>
    internal List<int> Range(CuteValue low, bool lowInclusive, CuteValue high, bool highInclusive)
    {
        EnsureSorted();

        var start = low.IsMissing ? 0 : LowerBound(low, lowInclusive);
        var rows = new List<int>();

        for (var i = start; i < _sortedKeys.Length; i++)
        {
            var key = _sortedKeys[i];
            if (!high.IsMissing)
            {
                var order = CuteValueComparer.Compare(key, high);
                if (order > 0 || (order == 0 && !highInclusive))
                {
                    break;
                }
            }

            rows.AddRange(_byKey[key].AsSpan());
        }

        return rows;
    }

    /// <summary>Every key in ascending order, with the rows under each.</summary>
    internal IEnumerable<(CuteValue Key, int[] Rows)> Ordered()
    {
        EnsureSorted();
        foreach (var key in _sortedKeys)
        {
            yield return (key, _byKey[key].ToArray());
        }
    }

    /// <summary>Rebuilds the index from scratch over a collection's live rows.</summary>
    internal void Rebuild(DocumentStore store)
    {
        Clear();

        var keys = new List<CuteValue>(4);
        var refs = store.Refs;
        for (var row = 0; row < refs.Length; row++)
        {
            if (refs[row].IsEmpty)
            {
                continue;
            }

            ExtractKeys(store.Read(row), keys);
            foreach (var key in keys)
            {
                Add(key, row);
            }
        }
    }

    private void EnsureSorted()
    {
        if (_sortedKeysValid)
        {
            return;
        }

        _sortedKeys = new CuteValue[_byKey.Count];
        _byKey.Keys.CopyTo(_sortedKeys, 0);
        Array.Sort(_sortedKeys, CuteValueEqualityComparer.Instance);
        _sortedKeysValid = true;
    }

    private int LowerBound(CuteValue low, bool inclusive)
    {
        var lo = 0;
        var hi = _sortedKeys.Length;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            var order = CuteValueComparer.Compare(_sortedKeys[mid], low);
            if (order < 0 || (order == 0 && !inclusive))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// The rows under one key. Almost every key in a real index has exactly one row, so the first
    /// is stored inline and the list is allocated only for genuine duplicates.
    /// </summary>
    private sealed class RowSet
    {
        private int _single;
        private List<int>? _many;

        internal RowSet(int row) => _single = row;

        internal int Count => _many?.Count ?? (_single >= 0 ? 1 : 0);

        internal void Add(int row)
        {
            if (_many is not null)
            {
                _many.Add(row);
                return;
            }

            _many = [_single, row];
            _single = -1;
        }

        internal bool Remove(int row)
        {
            if (_many is null)
            {
                if (_single != row)
                {
                    return false;
                }

                _single = -1;
                return true;
            }

            return _many.Remove(row);
        }

        internal ReadOnlySpan<int> AsSpan()
        {
            if (_many is not null)
            {
                return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_many);
            }

            return _single >= 0
                ? System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref _single, 1)
                : ReadOnlySpan<int>.Empty;
        }

        internal int[] ToArray() => AsSpan().ToArray();
    }
}
