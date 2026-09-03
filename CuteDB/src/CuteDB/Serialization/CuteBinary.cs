using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace CuteDB;

/// <summary>
/// CuteDB's binary document encoding — the format documents live in, both on disk and in memory.
/// </summary>
/// <remarks>
/// <para>
/// Every value is a one-byte <see cref="CuteType"/> tag followed by a payload. Scalars are fixed
/// width; strings and binaries carry a varint byte length; arrays and objects carry a 32-bit
/// payload length <em>before</em> their element count.
/// </para>
/// <para>
/// That leading length on containers is the whole point of the format. It means a reader looking
/// for <c>customer.city</c> can walk an object's keys and jump over any field it does not care
/// about with a single add, instead of parsing the subtree to find out where it ends. Reading one
/// field out of a deep document costs a few comparisons rather than a full decode, which is what
/// makes a filtering scan viable without an index — and what the Rust accelerator exploits to run
/// the same walk over raw memory.
/// </para>
/// <para>
/// The format is little-endian everywhere except <see cref="CuteId"/>, whose bytes are big-endian
/// by its own definition so that raw byte order matches value order.
/// </para>
/// </remarks>
public static class CuteBinary
{
    /// <summary>Encodes a value into <paramref name="writer"/>.</summary>
    public static void Write(CuteBufferWriter writer, CuteValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte((byte)value.Type);
        switch (value.Type)
        {
            case CuteType.Null:
            case CuteType.False:
            case CuteType.True:
                break;

            case CuteType.Int32:
                writer.WriteUInt32(unchecked((uint)value.AsInt32));
                break;

            case CuteType.Int64:
                writer.WriteUInt64(unchecked((ulong)value.AsInt64));
                break;

            case CuteType.Double:
                writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(value.AsDouble));
                break;

            case CuteType.DateTime:
                writer.WriteUInt64(unchecked((ulong)value.AsDateTime.Ticks));
                break;

            case CuteType.Guid:
            case CuteType.Decimal:
                value.GetRawBits(out var lo, out var hi);
                writer.WriteUInt64(lo);
                writer.WriteUInt64(hi);
                break;

            case CuteType.Id:
            {
                Span<byte> bytes = stackalloc byte[CuteId.Size];
                value.AsId.Write(bytes);
                writer.WriteBytes(bytes);
                break;
            }

            case CuteType.String:
                writer.WriteVarString(value.AsString);
                break;

            case CuteType.Binary:
            {
                var binary = value.AsBinary;
                writer.WriteVarUInt((uint)binary.Length);
                writer.WriteBytes(binary);
                break;
            }

            case CuteType.Array:
            {
                var array = value.AsArray;
                var lengthSlot = writer.ReserveUInt32();
                var bodyStart = writer.Length;
                writer.WriteVarUInt((uint)array.Count);
                foreach (var item in array.AsSpan())
                {
                    Write(writer, item);
                }

                writer.PatchUInt32(lengthSlot, (uint)(writer.Length - bodyStart));
                break;
            }

            case CuteType.Object:
            {
                var obj = value.AsObject;
                var lengthSlot = writer.ReserveUInt32();
                var bodyStart = writer.Length;
                writer.WriteVarUInt((uint)obj.Count);
                foreach (var (key, field) in obj)
                {
                    writer.WriteVarString(key);
                    Write(writer, field);
                }

                writer.PatchUInt32(lengthSlot, (uint)(writer.Length - bodyStart));
                break;
            }

            case CuteType.Missing:
                throw new CuteDbException("A missing value cannot be stored. Use null to record an explicit absence.");

            default:
                throw new CuteDbException($"Unknown value type {(byte)value.Type} cannot be encoded.");
        }
    }

    /// <summary>Encodes a value into a freshly allocated array.</summary>
    public static byte[] Encode(CuteValue value)
    {
        var writer = CuteBufferWriter.Rent();
        try
        {
            Write(writer, value);
            return writer.ToArray();
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    /// <summary>Decodes the value at the start of <paramref name="data"/>.</summary>
    public static CuteValue Decode(ReadOnlySpan<byte> data) => Read(data, out _);

    /// <summary>Decodes a document body into a <see cref="CuteDocument"/>.</summary>
    public static CuteDocument DecodeDocument(ReadOnlySpan<byte> data)
    {
        var value = Decode(data);
        return value.IsObject
            ? new CuteDocument(value.AsObject, assignId: false)
            : throw new CuteCorruptionException($"Expected an encoded object, found {value.Type.ToDisplayName()}.");
    }

    /// <summary>
    /// Decodes the value at the start of <paramref name="data"/> and reports how many bytes it
    /// occupied.
    /// </summary>
    public static CuteValue Read(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.IsEmpty)
        {
            throw new CuteCorruptionException("Ran out of bytes while reading a value tag.");
        }

        var type = (CuteType)data[0];
        var body = data[1..];
        switch (type)
        {
            case CuteType.Null:
                consumed = 1;
                return CuteValue.Null;

            case CuteType.False:
                consumed = 1;
                return CuteValue.Boolean(false);

            case CuteType.True:
                consumed = 1;
                return CuteValue.Boolean(true);

            case CuteType.Int32:
                Demand(body, 4, "Int32");
                consumed = 5;
                return CuteValue.Int32(BinaryPrimitives.ReadInt32LittleEndian(body));

            case CuteType.Int64:
                Demand(body, 8, "Int64");
                consumed = 9;
                return CuteValue.Int64(BinaryPrimitives.ReadInt64LittleEndian(body));

            case CuteType.Double:
                Demand(body, 8, "Double");
                consumed = 9;
                return CuteValue.Double(BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(body)));

            case CuteType.DateTime:
                Demand(body, 8, "DateTime");
                consumed = 9;
                return CuteValue.DateTime(new DateTime(BinaryPrimitives.ReadInt64LittleEndian(body), DateTimeKind.Utc));

            case CuteType.Guid:
                Demand(body, 16, "Guid");
                consumed = 17;
                return CuteValue.Guid(new Guid(body[..16]));

            case CuteType.Decimal:
                Demand(body, 16, "Decimal");
                consumed = 17;
                return CuteValue.Decimal(CuteValue.DecimalFromBits(
                    BinaryPrimitives.ReadUInt64LittleEndian(body),
                    BinaryPrimitives.ReadUInt64LittleEndian(body[8..])));

            case CuteType.Id:
                Demand(body, CuteId.Size, "Id");
                consumed = 1 + CuteId.Size;
                return CuteValue.Id(CuteId.Read(body));

            case CuteType.String:
            {
                var length = (int)ReadVarUInt(body, out var prefix);
                Demand(body[prefix..], length, "String");
                consumed = 1 + prefix + length;
                return CuteValue.String(Encoding.UTF8.GetString(body.Slice(prefix, length)));
            }

            case CuteType.Binary:
            {
                var length = (int)ReadVarUInt(body, out var prefix);
                Demand(body[prefix..], length, "Binary");
                consumed = 1 + prefix + length;
                return CuteValue.Binary(body.Slice(prefix, length).ToArray());
            }

            case CuteType.Array:
            {
                Demand(body, 4, "Array header");
                var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
                Demand(body[4..], payloadLength, "Array body");
                consumed = 5 + payloadLength;

                var payload = body.Slice(4, payloadLength);
                var count = (int)ReadVarUInt(payload, out var countWidth);
                var cursor = countWidth;
                var array = new CuteArray(count);
                for (var i = 0; i < count; i++)
                {
                    array.Add(Read(payload[cursor..], out var itemLength));
                    cursor += itemLength;
                }

                return CuteValue.Array(array);
            }

            case CuteType.Object:
            {
                Demand(body, 4, "Object header");
                var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
                Demand(body[4..], payloadLength, "Object body");
                consumed = 5 + payloadLength;

                var payload = body.Slice(4, payloadLength);
                var count = (int)ReadVarUInt(payload, out var countWidth);
                var cursor = countWidth;
                var obj = new CuteObject(count);
                for (var i = 0; i < count; i++)
                {
                    var keyLength = (int)ReadVarUInt(payload[cursor..], out var keyPrefix);
                    cursor += keyPrefix;
                    Demand(payload[cursor..], keyLength, "Object key");
                    var key = Encoding.UTF8.GetString(payload.Slice(cursor, keyLength));
                    cursor += keyLength;

                    obj.Set(key, Read(payload[cursor..], out var fieldLength));
                    cursor += fieldLength;
                }

                return CuteValue.Object(obj);
            }

            default:
                throw new CuteCorruptionException($"Unknown value tag 0x{(byte)type:X2}.");
        }
    }

    /// <summary>
    /// Returns the total encoded length of the value at the start of <paramref name="data"/>
    /// without decoding it.
    /// </summary>
    public static int Skip(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new CuteCorruptionException("Ran out of bytes while skipping a value.");
        }

        var type = (CuteType)data[0];
        var body = data[1..];
        switch (type)
        {
            case CuteType.Null:
            case CuteType.False:
            case CuteType.True:
                return 1;

            case CuteType.Int32:
                return 5;

            case CuteType.Int64:
            case CuteType.Double:
            case CuteType.DateTime:
                return 9;

            case CuteType.Guid:
            case CuteType.Decimal:
                return 17;

            case CuteType.Id:
                return 1 + CuteId.Size;

            case CuteType.String:
            case CuteType.Binary:
            {
                var length = (int)ReadVarUInt(body, out var prefix);
                return 1 + prefix + length;
            }

            case CuteType.Array:
            case CuteType.Object:
                Demand(body, 4, "container header");
                return 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(body);

            default:
                throw new CuteCorruptionException($"Unknown value tag 0x{(byte)type:X2}.");
        }
    }

    /// <summary>
    /// Finds a field inside an encoded object without decoding the fields it skips over.
    /// <paramref name="value"/> must start at an object's tag byte.
    /// </summary>
    public static bool TryGetField(ReadOnlySpan<byte> value, ReadOnlySpan<byte> utf8Key, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (value.IsEmpty || (CuteType)value[0] != CuteType.Object)
        {
            return false;
        }

        var body = value[1..];
        Demand(body, 4, "Object header");
        var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var payload = body.Slice(4, payloadLength);

        var count = (int)ReadVarUInt(payload, out var cursor);
        for (var i = 0; i < count; i++)
        {
            var keyLength = (int)ReadVarUInt(payload[cursor..], out var keyPrefix);
            cursor += keyPrefix;

            var candidate = payload.Slice(cursor, keyLength);
            cursor += keyLength;

            var fieldValue = payload[cursor..];
            if (candidate.SequenceEqual(utf8Key))
            {
                field = fieldValue[..Skip(fieldValue)];
                return true;
            }

            cursor += Skip(fieldValue);
        }

        return false;
    }

    /// <summary>
    /// Indexes into an encoded array without decoding the elements it skips over. Negative indices
    /// count from the end. <paramref name="value"/> must start at an array's tag byte.
    /// </summary>
    public static bool TryGetElement(ReadOnlySpan<byte> value, int index, out ReadOnlySpan<byte> element)
    {
        element = default;
        if (value.IsEmpty || (CuteType)value[0] != CuteType.Array)
        {
            return false;
        }

        var body = value[1..];
        Demand(body, 4, "Array header");
        var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var payload = body.Slice(4, payloadLength);

        var count = (int)ReadVarUInt(payload, out var cursor);
        var effective = index < 0 ? count + index : index;
        if ((uint)effective >= (uint)count)
        {
            return false;
        }

        for (var i = 0; i < effective; i++)
        {
            cursor += Skip(payload[cursor..]);
        }

        var target = payload[cursor..];
        element = target[..Skip(target)];
        return true;
    }

    /// <summary>Returns the element count of an encoded array, or -1 when the value is not an array.</summary>
    public static int GetArrayLength(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || (CuteType)value[0] != CuteType.Array)
        {
            return -1;
        }

        var body = value[1..];
        Demand(body, 4, "Array header");
        var payload = body.Slice(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(body));
        return (int)ReadVarUInt(payload, out _);
    }

    /// <summary>
    /// Enumerates the elements of an encoded array as slices, without decoding any of them.
    /// </summary>
    public static ArrayElementEnumerator EnumerateArray(ReadOnlySpan<byte> value) => new(value);

    /// <summary>Reads an unsigned LEB128 varint and reports its width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadVarUInt(ReadOnlySpan<byte> data, out int width)
    {
        uint result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            if (i >= data.Length)
            {
                throw new CuteCorruptionException("Ran out of bytes while reading a varint.");
            }

            var b = data[i];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                width = i + 1;
                return result;
            }

            shift += 7;
        }

        throw new CuteCorruptionException("Varint is longer than five bytes.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Demand(ReadOnlySpan<byte> data, int required, string what)
    {
        if (data.Length < required)
        {
            throw new CuteCorruptionException($"Truncated {what}: needed {required} bytes, {data.Length} remain.");
        }
    }

    /// <summary>Walks the elements of an encoded array as raw slices.</summary>
    public ref struct ArrayElementEnumerator
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly int _count;
        private int _cursor;
        private int _position;

        internal ArrayElementEnumerator(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty || (CuteType)value[0] != CuteType.Array)
            {
                _payload = default;
                _count = 0;
                _cursor = 0;
                _position = 0;
                Current = default;
                return;
            }

            var body = value[1..];
            _payload = body.Slice(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(body));
            _count = (int)ReadVarUInt(_payload, out _cursor);
            _position = 0;
            Current = default;
        }

        /// <summary>The element the enumerator is positioned on.</summary>
        public ReadOnlySpan<byte> Current { get; private set; }

        /// <summary>The number of elements in the array.</summary>
        public readonly int Count => _count;

        /// <summary>Enables <c>foreach</c>.</summary>
        public readonly ArrayElementEnumerator GetEnumerator() => this;

        /// <summary>Advances to the next element.</summary>
        public bool MoveNext()
        {
            if (_position >= _count)
            {
                return false;
            }

            var remaining = _payload[_cursor..];
            var length = Skip(remaining);
            Current = remaining[..length];
            _cursor += length;
            _position++;
            return true;
        }
    }
}
