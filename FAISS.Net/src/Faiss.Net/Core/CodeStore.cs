namespace Faiss.Net.Core;

/// <summary>
/// Growable store of fixed-size codes in one contiguous <see cref="byte"/> array — the compressed
/// counterpart of <see cref="VectorStore"/>, used by the flat code indexes (<see cref="IndexPQ"/>,
/// <see cref="IndexScalarQuantizer"/>).
/// <para>
/// Same concurrency contract as <see cref="VectorStore"/>: one writer, many readers. The array and
/// count are published together; appends write past the published count before publishing, and
/// anything that rewrites live codes builds a new array.
/// </para>
/// </summary>
public sealed class CodeStore
{
    private sealed class State(byte[] data, int count)
    {
        public readonly byte[] Data = data;
        public readonly int Count = count;
    }

    private State _state = new([], 0);

    /// <summary>Bytes per code.</summary>
    public int CodeSize { get; }

    public CodeStore(int codeSize)
    {
        if (codeSize <= 0) throw new ArgumentOutOfRangeException(nameof(codeSize));
        CodeSize = codeSize;
    }

    /// <summary>Codes stored.</summary>
    public int Count => Volatile.Read(ref _state).Count;

    /// <summary>Backing array and live count, read together; see <see cref="VectorStore.Snapshot"/>.</summary>
    public (byte[] Data, int Count) Snapshot()
    {
        var state = Volatile.Read(ref _state);
        return (state.Data, state.Count);
    }

    /// <summary>All live codes.</summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        var state = Volatile.Read(ref _state);
        return state.Data.AsSpan(0, state.Count * CodeSize);
    }

    /// <summary>One code by position.</summary>
    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            var state = Volatile.Read(ref _state);
            if ((uint)index >= (uint)state.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return state.Data.AsSpan(index * CodeSize, CodeSize);
        }
    }

    /// <summary>
    /// Appends <paramref name="n"/> codes produced by <paramref name="encode"/>, which writes directly
    /// into the reserved slots so the codes are never staged through a second buffer.
    /// </summary>
    public void Append(int n, SpanAction encode)
    {
        if (n == 0) return;
        var state = _state;
        long needed = (long)(state.Count + n) * CodeSize;
        byte[] data = state.Data;
        if (needed > data.Length)
        {
            long grown = Math.Max(needed, data.Length + (data.Length >> 1));
            if (grown > Array.MaxLength) grown = needed;
            data = new byte[grown];
            state.Data.AsSpan(0, state.Count * CodeSize).CopyTo(data);
        }
        encode(data.AsSpan(state.Count * CodeSize, n * CodeSize));
        Volatile.Write(ref _state, new State(data, state.Count + n));
    }

    /// <summary>Writes into the slots reserved by <see cref="Append"/>.</summary>
    public delegate void SpanAction(Span<byte> codes);

    /// <summary>Order-preserving removal into a new array. Returns how many codes were removed.</summary>
    public int Compact(Func<int, bool> keep)
    {
        var state = _state;
        var data = new byte[state.Data.Length];
        int write = 0;
        for (int read = 0; read < state.Count; read++)
        {
            if (!keep(read)) continue;
            state.Data.AsSpan(read * CodeSize, CodeSize).CopyTo(data.AsSpan(write * CodeSize, CodeSize));
            write++;
        }
        int removed = state.Count - write;
        if (removed > 0) Volatile.Write(ref _state, new State(data, write));
        return removed;
    }

    /// <summary>Drops every code, releasing the buffer.</summary>
    public void Clear() => Volatile.Write(ref _state, new State([], 0));

    /// <summary>Replaces the contents wholesale, for deserialization.</summary>
    public void Load(byte[] data, int count) => Volatile.Write(ref _state, new State(data, count));

    /// <summary>Allocated bytes.</summary>
    public long MemoryUsage => Volatile.Read(ref _state).Data.Length;
}
