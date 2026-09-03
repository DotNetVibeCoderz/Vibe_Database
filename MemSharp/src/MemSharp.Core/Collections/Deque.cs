using System.Runtime.CompilerServices;

namespace MemSharp.Collections;

/// <summary>
/// A growable ring buffer: O(1) amortised push and pop at both ends, O(1) indexing, one array.
/// </summary>
/// <remarks>
/// This exists because a list type built on <see cref="List{T}"/> makes <c>LPUSH</c> O(n) - every
/// left-push shifts the whole backing array. That is the single worst asymptotic in the original
/// MemSharp engine, and it is quadratic in exactly the pattern lists are used for (a capped feed
/// that is pushed at the head and trimmed at the tail).
///
/// Not thread-safe. Callers hold the owning shard's lock.
/// </remarks>
internal sealed class Deque<T>
{
    private T[] _items;
    private int _head;   // index of the first element
    private int _count;

    public Deque(int capacity = 4)
    {
        _items = capacity > 0 ? new T[NextPowerOfTwo(capacity)] : [];
    }

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[Wrap(_head + index)];
        }
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            _items[Wrap(_head + index)] = value;
        }
    }

    public void PushFront(T item)
    {
        EnsureCapacity(_count + 1);
        _head = Wrap(_head - 1);
        _items[_head] = item;
        _count++;
    }

    public void PushBack(T item)
    {
        EnsureCapacity(_count + 1);
        _items[Wrap(_head + _count)] = item;
        _count++;
    }

    public bool TryPopFront(out T item)
    {
        if (_count == 0) { item = default!; return false; }
        item = _items[_head];
        _items[_head] = default!;          // release the reference so the GC can collect it
        _head = Wrap(_head + 1);
        _count--;
        return true;
    }

    public bool TryPopBack(out T item)
    {
        if (_count == 0) { item = default!; return false; }
        int tail = Wrap(_head + _count - 1);
        item = _items[tail];
        _items[tail] = default!;
        _count--;
        return true;
    }

    /// <summary>Copies <paramref name="count"/> items starting at <paramref name="start"/>.</summary>
    public T[] Slice(int start, int count)
    {
        if (count <= 0) return [];
        var result = new T[count];
        int first = Wrap(_head + start);
        int untilEnd = Math.Min(count, _items.Length - first);
        Array.Copy(_items, first, result, 0, untilEnd);
        if (untilEnd < count) Array.Copy(_items, 0, result, untilEnd, count - untilEnd);
        return result;
    }

    /// <summary>Discards everything outside <c>[start, start + count)</c>, keeping the same array.</summary>
    public void KeepRange(int start, int count)
    {
        if (count <= 0) { Clear(); return; }
        if (start == 0 && count == _count) return;

        // Clearing the discarded slots matters for reference types: the array survives the trim, so
        // anything left behind would be kept alive by it.
        for (int i = 0; i < start; i++) _items[Wrap(_head + i)] = default!;
        for (int i = start + count; i < _count; i++) _items[Wrap(_head + i)] = default!;

        _head = Wrap(_head + start);
        _count = count;
    }

    public void Clear()
    {
        if (_count > 0)
        {
            int first = _head;
            int untilEnd = Math.Min(_count, _items.Length - first);
            Array.Clear(_items, first, untilEnd);
            if (untilEnd < _count) Array.Clear(_items, 0, _count - untilEnd);
        }
        _head = 0;
        _count = 0;
    }

    public IEnumerable<T> Enumerate()
    {
        for (int i = 0; i < _count; i++) yield return _items[Wrap(_head + i)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Wrap(int index)
    {
        // _items.Length is always a power of two, so the mask replaces two branches and a modulo.
        int mask = _items.Length - 1;
        return index & mask;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _items.Length) return;
        int capacity = Math.Max(4, _items.Length == 0 ? 4 : _items.Length * 2);
        while (capacity < required) capacity *= 2;

        var grown = new T[capacity];
        if (_count > 0)
        {
            int untilEnd = Math.Min(_count, _items.Length - _head);
            Array.Copy(_items, _head, grown, 0, untilEnd);
            if (untilEnd < _count) Array.Copy(_items, 0, grown, untilEnd, _count - untilEnd);
        }
        _items = grown;
        _head = 0;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 4;
        while (result < value) result *= 2;
        return result;
    }
}
