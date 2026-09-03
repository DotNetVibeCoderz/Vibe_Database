using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace CuteDB;

/// <summary>
/// A growable byte buffer backed by <see cref="ArrayPool{T}"/>, used for every encode path in
/// CuteDB.
/// </summary>
/// <remarks>
/// <para>
/// Encoding a document means writing a few dozen small pieces and then handing the result to the
/// storage engine, which copies it into a slab and never looks at the buffer again. A pooled
/// buffer that is rented, filled, copied out and returned keeps that whole path off the GC heap:
/// bulk-inserting a million documents allocates a handful of arrays rather than a million.
/// </para>
/// <para>
/// Instances are not thread-safe. The engine keeps one per writing thread via
/// <see cref="Rent"/>/<see cref="Return"/> rather than sharing one.
/// </para>
/// </remarks>
public sealed class CuteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultCapacity = 4 * 1024;
    private const int MaxPooledCapacity = 1 * 1024 * 1024;

    [ThreadStatic]
    private static CuteBufferWriter? _cached;

    private byte[] _buffer;
    private int _written;

    /// <summary>Creates a writer with an initial capacity.</summary>
    public CuteBufferWriter(int capacity = DefaultCapacity)
        => _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));

    /// <summary>The number of bytes written so far.</summary>
    public int Length => _written;

    /// <summary>The bytes written so far. Only valid until the next write.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>The bytes written so far, as memory.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// Borrows a writer for the current thread. Returning it with <see cref="Return"/> makes it
    /// available again; forgetting to is not fatal, it just costs an allocation next time.
    /// </summary>
    public static CuteBufferWriter Rent()
    {
        var writer = _cached;
        if (writer is null)
        {
            return new CuteBufferWriter();
        }

        _cached = null;
        writer.Reset();
        return writer;
    }

    /// <summary>Gives a rented writer back to the current thread's slot.</summary>
    public static void Return(CuteBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // An oversized buffer that was grown for one giant document should not be kept alive
        // forever, so it is dropped rather than cached.
        if (writer._buffer.Length > MaxPooledCapacity)
        {
            writer.Dispose();
            return;
        }

        writer.Reset();
        _cached = writer;
    }

    /// <summary>Rewinds to empty without releasing the buffer.</summary>
    public void Reset() => _written = 0;

    /// <summary>Copies the written bytes into a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint <= 0 ? 1 : sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint <= 0 ? 1 : sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Appends a single byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_written++] = value;
    }

    /// <summary>Appends raw bytes.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_written));
        _written += value.Length;
    }

    /// <summary>Appends a little-endian 32-bit integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_written), value);
        _written += sizeof(uint);
    }

    /// <summary>Appends a little-endian 64-bit integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong value)
    {
        EnsureCapacity(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_written), value);
        _written += sizeof(ulong);
    }

    /// <summary>Appends an unsigned LEB128 varint.</summary>
    public void WriteVarUInt(uint value)
    {
        EnsureCapacity(5);
        while (value >= 0x80)
        {
            _buffer[_written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        _buffer[_written++] = (byte)value;
    }

    /// <summary>Appends a UTF-8 string prefixed with its byte length as a varint.</summary>
    public void WriteVarString(string value)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);

        // Reserve worst case for both the length prefix and the payload, then write the real
        // length once it is known. Strings short enough for a one-byte prefix are the common case,
        // so the prefix is written first at its actual width and the payload moved only if the
        // guess was wrong.
        EnsureCapacity(maxBytes + 5);

        var byteCount = Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_written + 5));
        var prefixWidth = VarUIntWidth((uint)byteCount);
        if (prefixWidth != 5)
        {
            _buffer.AsSpan(_written + 5, byteCount).CopyTo(_buffer.AsSpan(_written + prefixWidth));
        }

        var value2 = (uint)byteCount;
        var cursor = _written;
        while (value2 >= 0x80)
        {
            _buffer[cursor++] = (byte)(value2 | 0x80);
            value2 >>= 7;
        }

        _buffer[cursor] = (byte)value2;
        _written += prefixWidth + byteCount;
    }

    /// <summary>
    /// Reserves four bytes for a length that is only known once the body has been written, and
    /// returns the offset to patch with <see cref="PatchUInt32"/>.
    /// </summary>
    public int ReserveUInt32()
    {
        var offset = _written;
        WriteUInt32(0);
        return offset;
    }

    /// <summary>Writes a 32-bit value into a slot previously reserved by <see cref="ReserveUInt32"/>.</summary>
    public void PatchUInt32(int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset), value);

    /// <summary>Returns the byte width of a value encoded as an unsigned LEB128 varint.</summary>
    public static int VarUIntWidth(uint value) => value switch
    {
        < 1u << 7 => 1,
        < 1u << 14 => 2,
        < 1u << 21 => 3,
        < 1u << 28 => 4,
        _ => 5,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int additional)
    {
        if (_written + additional > _buffer.Length)
        {
            Grow(additional);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additional)
    {
        var required = _written + additional;
        var capacity = Math.Max(_buffer.Length * 2, required);
        var replacement = ArrayPool<byte>.Shared.Rent(capacity);
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
}
