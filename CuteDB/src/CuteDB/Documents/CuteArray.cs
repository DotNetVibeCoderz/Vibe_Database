using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CuteDB;

/// <summary>
/// An ordered list of <see cref="CuteValue"/>, the array half of CuteDB's document model.
/// </summary>
[DebuggerDisplay("Array ({Count} items)")]
public sealed class CuteArray : IList<CuteValue>
{
    private readonly List<CuteValue> _items;

    /// <summary>Creates an empty array.</summary>
    public CuteArray() => _items = [];

    /// <summary>Creates an empty array with room for <paramref name="capacity"/> items.</summary>
    public CuteArray(int capacity) => _items = new List<CuteValue>(capacity);

    /// <summary>Creates an array from an existing sequence.</summary>
    public CuteArray(IEnumerable<CuteValue> items) => _items = [.. items];

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public CuteValue this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>
    /// The items as a span, so scans and aggregates can walk the array without going through the
    /// enumerator.
    /// </summary>
    public ReadOnlySpan<CuteValue> AsSpan() => CollectionsMarshal.AsSpan(_items);

    /// <inheritdoc />
    public void Add(CuteValue item) => _items.Add(item);

    /// <summary>Appends several items at once.</summary>
    public void AddRange(IEnumerable<CuteValue> items) => _items.AddRange(items);

    /// <inheritdoc />
    public void Insert(int index, CuteValue item) => _items.Insert(index, item);

    /// <inheritdoc />
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <inheritdoc />
    public bool Remove(CuteValue item) => _items.Remove(item);

    /// <inheritdoc />
    public void Clear() => _items.Clear();

    /// <inheritdoc />
    public bool Contains(CuteValue item) => IndexOf(item) >= 0;

    /// <inheritdoc />
    public int IndexOf(CuteValue item)
    {
        var items = CollectionsMarshal.AsSpan(_items);
        for (var i = 0; i < items.Length; i++)
        {
            if (CuteValueComparer.Equal(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public void CopyTo(CuteValue[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>Returns a deep copy: nested objects and arrays are cloned too.</summary>
    public CuteArray DeepClone()
    {
        var clone = new CuteArray(_items.Count);
        foreach (var item in CollectionsMarshal.AsSpan(_items))
        {
            clone.Add(CuteValueCloner.DeepClone(item));
        }

        return clone;
    }

    /// <inheritdoc />
    public IEnumerator<CuteValue> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => CuteJson.Write(CuteValue.Array(this), indented: false);
}

/// <summary>Deep-copy helper shared by <see cref="CuteObject"/> and <see cref="CuteArray"/>.</summary>
internal static class CuteValueCloner
{
    internal static CuteValue DeepClone(CuteValue value) => value.Type switch
    {
        CuteType.Object => CuteValue.Object(value.AsObject.DeepClone()),
        CuteType.Array => CuteValue.Array(value.AsArray.DeepClone()),

        // Binary is the only remaining mutable payload; strings and scalars can be shared safely.
        CuteType.Binary => CuteValue.Binary((byte[])value.AsBinary.Clone()),
        _ => value,
    };
}
