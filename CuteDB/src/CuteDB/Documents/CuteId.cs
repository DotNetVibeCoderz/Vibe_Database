using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CuteDB;

/// <summary>
/// The 12-byte primary key CuteDB assigns to every document.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>[4 bytes big-endian unix seconds][5 bytes per-process random][3 bytes
/// big-endian counter]</c>. Big-endian ordering is deliberate: it makes the raw bytes sort in the
/// same order as the values, so a range index over ids is also a range index over creation time,
/// and no separate timestamp field is needed to page through a collection in insertion order.
/// </para>
/// <para>
/// Ids are generated without coordination. The random block is drawn once per process, so two
/// processes writing to the same file will not collide, and the counter keeps ids unique within a
/// process even when many are created inside the same second.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public readonly struct CuteId : IEquatable<CuteId>, IComparable<CuteId>, ISpanFormattable
{
    /// <summary>Size of an id in bytes.</summary>
    public const int Size = 12;

    private static readonly ulong ProcessRandom = CreateProcessRandom();
    private static int _counter = RandomNumberGenerator.GetInt32(0, 0x00FFFFFF);

    private readonly uint _timestamp;
    private readonly uint _randomHigh;
    private readonly uint _randomLowAndCounter;

    private CuteId(uint timestamp, uint randomHigh, uint randomLowAndCounter)
    {
        _timestamp = timestamp;
        _randomHigh = randomHigh;
        _randomLowAndCounter = randomLowAndCounter;
    }

    /// <summary>An id whose bytes are all zero, used as the "no id yet" sentinel.</summary>
    public static CuteId Empty => default;

    /// <summary>True when this is <see cref="Empty"/>.</summary>
    public bool IsEmpty => _timestamp == 0 && _randomHigh == 0 && _randomLowAndCounter == 0;

    /// <summary>The moment this id was created, to one-second resolution.</summary>
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeSeconds(_timestamp);

    /// <summary>Creates a new unique id.</summary>
    public static CuteId NewId()
    {
        var seconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = (uint)(Interlocked.Increment(ref _counter) & 0x00FFFFFF);

        Span<byte> buffer = stackalloc byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, seconds);
        BinaryPrimitives.WriteUInt64BigEndian(buffer[4..], (ProcessRandom << 24) | counter);
        return Read(buffer);
    }

    /// <summary>Reads an id from exactly <see cref="Size"/> bytes.</summary>
    public static CuteId Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"A CuteId needs {Size} bytes, got {source.Length}.", nameof(source));
        }

        return new CuteId(
            BinaryPrimitives.ReadUInt32BigEndian(source),
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[8..]));
    }

    /// <summary>Writes this id into <paramref name="destination"/> as <see cref="Size"/> bytes.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"A CuteId needs {Size} bytes, got {destination.Length}.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination, _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], _randomHigh);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], _randomLowAndCounter);
    }

    /// <summary>Returns the id as a fresh 12-byte array.</summary>
    public byte[] ToByteArray()
    {
        var bytes = new byte[Size];
        Write(bytes);
        return bytes;
    }

    /// <summary>Parses the 24-character lowercase hex form produced by <see cref="ToString()"/>.</summary>
    public static CuteId Parse(string value)
        => TryParse(value, out var id)
            ? id
            : throw new FormatException($"'{value}' is not a CuteId. Expected 24 hex characters.");

    /// <summary>Parses the 24-character hex form, returning false instead of throwing.</summary>
    public static bool TryParse(ReadOnlySpan<char> value, out CuteId id)
    {
        id = default;
        if (value.Length != Size * 2)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[Size];
        for (var i = 0; i < Size; i++)
        {
            if (!TryHex(value[i * 2], out var high) || !TryHex(value[(i * 2) + 1], out var low))
            {
                return false;
            }

            buffer[i] = (byte)((high << 4) | low);
        }

        id = Read(buffer);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(Size * 2, this, static (chars, id) => id.TryFormat(chars, out _, default, null));

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        charsWritten = 0;
        if (destination.Length < Size * 2)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Size];
        Write(bytes);

        const string Hex = "0123456789abcdef";
        for (var i = 0; i < Size; i++)
        {
            destination[i * 2] = Hex[bytes[i] >> 4];
            destination[(i * 2) + 1] = Hex[bytes[i] & 0x0F];
        }

        charsWritten = Size * 2;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CuteId other)
        => _timestamp == other._timestamp
            && _randomHigh == other._randomHigh
            && _randomLowAndCounter == other._randomLowAndCounter;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is CuteId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_timestamp, _randomHigh, _randomLowAndCounter);

    /// <summary>Orders ids by their raw big-endian bytes, which is also creation order.</summary>
    public int CompareTo(CuteId other)
    {
        var byTimestamp = _timestamp.CompareTo(other._timestamp);
        if (byTimestamp != 0)
        {
            return byTimestamp;
        }

        var byHigh = _randomHigh.CompareTo(other._randomHigh);
        return byHigh != 0 ? byHigh : _randomLowAndCounter.CompareTo(other._randomLowAndCounter);
    }

    public static bool operator ==(CuteId left, CuteId right) => left.Equals(right);

    public static bool operator !=(CuteId left, CuteId right) => !left.Equals(right);

    public static bool operator <(CuteId left, CuteId right) => left.CompareTo(right) < 0;

    public static bool operator >(CuteId left, CuteId right) => left.CompareTo(right) > 0;

    public static bool operator <=(CuteId left, CuteId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CuteId left, CuteId right) => left.CompareTo(right) >= 0;

    private static ulong CreateProcessRandom()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        // Only the low 40 bits are used; the counter occupies the remaining 24.
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes) & 0xFF_FFFF_FFFFUL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }
}
