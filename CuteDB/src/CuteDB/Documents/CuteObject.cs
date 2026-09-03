using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace CuteDB;

/// <summary>
/// An ordered map of string keys to <see cref="CuteValue"/>, the object half of CuteDB's document
/// model.
/// </summary>
/// <remarks>
/// <para>
/// Insertion order is preserved, because a document that round-trips through CuteDB should come
/// back looking like the JSON that went in.
/// </para>
/// <para>
/// Small objects — the overwhelming majority — are backed by a plain list and looked up by linear
/// scan, which beats hashing for the handful of fields a typical document has and costs one
/// allocation instead of two. A key index is built lazily only once an object grows past
/// <see cref="IndexThreshold"/> fields.
/// </para>
/// </remarks>
[DebuggerDisplay("Object ({Count} fields)")]
public sealed class CuteObject : IEnumerable<KeyValuePair<string, CuteValue>>
{
    /// <summary>Field count past which lookups switch from a linear scan to a hash index.</summary>
    private const int IndexThreshold = 12;

    private readonly List<KeyValuePair<string, CuteValue>> _entries;
    private Dictionary<string, int>? _index;

    /// <summary>Creates an empty object.</summary>
    public CuteObject() => _entries = [];

    /// <summary>Creates an empty object with room for <paramref name="capacity"/> fields.</summary>
    public CuteObject(int capacity) => _entries = new List<KeyValuePair<string, CuteValue>>(capacity);

    /// <summary>Creates an object from existing field pairs, keeping their order.</summary>
    public CuteObject(IEnumerable<KeyValuePair<string, CuteValue>> fields)
    {
        _entries = [.. fields];
        if (_entries.Count > IndexThreshold)
        {
            BuildIndex();
        }
    }

    /// <summary>The number of fields.</summary>
    public int Count => _entries.Count;

    /// <summary>The field names, in insertion order.</summary>
    public IEnumerable<string> Keys => _entries.Select(static entry => entry.Key);

    /// <summary>The field values, in insertion order.</summary>
    public IEnumerable<CuteValue> Values => _entries.Select(static entry => entry.Value);

    /// <summary>
    /// Gets or sets a field. Reading an absent field yields <see cref="CuteValue.Missing"/> rather
    /// than throwing; setting one appends it if it is not already present.
    /// </summary>
    public CuteValue this[string key]
    {
        get => TryGetValue(key, out var value) ? value : CuteValue.Missing;
        set => Set(key, value);
    }

    /// <summary>Gets the field at <paramref name="ordinal"/> in insertion order.</summary>
    public KeyValuePair<string, CuteValue> GetAt(int ordinal) => _entries[ordinal];

    /// <summary>Looks up a field.</summary>
    public bool TryGetValue(string key, out CuteValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        var ordinal = IndexOf(key);
        if (ordinal < 0)
        {
            value = CuteValue.Missing;
            return false;
        }

        value = _entries[ordinal].Value;
        return true;
    }

    /// <summary>True when the field exists, even if its value is null.</summary>
    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <summary>
    /// Sets a field, replacing any existing value under that key and keeping its original
    /// position, or appending it at the end when the key is new.
    /// </summary>
    public CuteObject Set(string key, CuteValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        var ordinal = IndexOf(key);
        if (ordinal >= 0)
        {
            _entries[ordinal] = new KeyValuePair<string, CuteValue>(key, value);
            return this;
        }

        _entries.Add(new KeyValuePair<string, CuteValue>(key, value));
        if (_index is not null)
        {
            _index[key] = _entries.Count - 1;
        }
        else if (_entries.Count > IndexThreshold)
        {
            BuildIndex();
        }

        return this;
    }

    /// <summary>Alias for <see cref="Set(string, CuteValue)"/> that reads better when building up a document.</summary>
    public CuteObject Add(string key, CuteValue value) => Set(key, value);

    /// <summary>Removes a field. Returns false when it was not there.</summary>
    public bool Remove(string key)
    {
        var ordinal = IndexOf(key);
        if (ordinal < 0)
        {
            return false;
        }

        _entries.RemoveAt(ordinal);

        // Removing shifts every later ordinal, so the index is rebuilt rather than patched.
        _index = null;
        if (_entries.Count > IndexThreshold)
        {
            BuildIndex();
        }

        return true;
    }

    /// <summary>Removes every field.</summary>
    public void Clear()
    {
        _entries.Clear();
        _index = null;
    }

    /// <summary>Returns a deep copy: nested objects and arrays are cloned too.</summary>
    public CuteObject DeepClone()
    {
        var clone = new CuteObject(_entries.Count);
        foreach (var (key, value) in _entries)
        {
            clone.Set(key, CuteValueCloner.DeepClone(value));
        }

        return clone;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, CuteValue>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => CuteJson.Write(CuteValue.Object(this), indented: false);

    private int IndexOf(string key)
    {
        if (_index is not null)
        {
            return _index.TryGetValue(key, out var ordinal) ? ordinal : -1;
        }

        var entries = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_entries);
        for (var i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    [MemberNotNull(nameof(_index))]
    private void BuildIndex()
    {
        var index = new Dictionary<string, int>(_entries.Count, StringComparer.Ordinal);
        for (var i = 0; i < _entries.Count; i++)
        {
            index[_entries[i].Key] = i;
        }

        _index = index;
    }
}
