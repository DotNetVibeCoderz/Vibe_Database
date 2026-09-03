using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CuteDB;

/// <summary>
/// A single JSON-shaped value: null, bool, number, string, binary, array or object, plus the
/// three extra scalar types CuteDB stores natively (<see cref="DateTime"/>, <see cref="Guid"/>
/// and <see cref="CuteId"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is a struct with two inline 64-bit payload slots and one object reference. Every scalar —
/// including <see cref="decimal"/> and <see cref="Guid"/>, which are 16 bytes — lives entirely in
/// the inline slots, so building a document out of scalars allocates nothing per field. Only
/// strings, binaries, arrays and objects touch the reference slot.
/// </para>
/// <para>
/// <see cref="CuteType.Missing"/> is a first-class value here. A path that does not resolve
/// returns Missing rather than Null, which is what lets CuteQL distinguish a field that was
/// explicitly set to <c>null</c> from a field that was never written.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToDebugString(),nq}")]
public readonly struct CuteValue : IEquatable<CuteValue>, IComparable<CuteValue>
{
    private readonly ulong _lo;
    private readonly ulong _hi;
    private readonly object? _reference;
    private readonly CuteType _type;

    private CuteValue(CuteType type, ulong lo = 0, ulong hi = 0, object? reference = null)
    {
        _type = type;
        _lo = lo;
        _hi = hi;
        _reference = reference;
    }

    /// <summary>An explicit JSON null.</summary>
    public static CuteValue Null => new(CuteType.Null);

    /// <summary>The value returned when a path does not resolve.</summary>
    public static CuteValue Missing => new(CuteType.Missing);

    /// <summary>An empty object.</summary>
    public static CuteValue EmptyObject => new(CuteType.Object, reference: new CuteObject());

    /// <summary>An empty array.</summary>
    public static CuteValue EmptyArray => new(CuteType.Array, reference: new CuteArray());

    /// <summary>The type of this value.</summary>
    public CuteType Type => _type;

    /// <summary>True when the path that produced this value did not resolve.</summary>
    public bool IsMissing => _type == CuteType.Missing;

    /// <summary>True for an explicit null. Absent fields are <see cref="IsMissing"/> instead.</summary>
    public bool IsNull => _type == CuteType.Null;

    /// <summary>True when this value is either explicitly null or absent.</summary>
    public bool IsNullOrMissing => _type is CuteType.Null or CuteType.Missing;

    /// <summary>True when this value carries an actual value (not null and not missing).</summary>
    public bool HasValue => _type is not (CuteType.Null or CuteType.Missing);

    /// <summary>True for the four numeric types.</summary>
    public bool IsNumber => _type.IsNumeric();

    /// <summary>True for <see cref="CuteType.Object"/>.</summary>
    public bool IsObject => _type == CuteType.Object;

    /// <summary>True for <see cref="CuteType.Array"/>.</summary>
    public bool IsArray => _type == CuteType.Array;

    // ---------------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------------

    /// <summary>Creates a boolean value.</summary>
    public static CuteValue Boolean(bool value) => new(value ? CuteType.True : CuteType.False);

    /// <summary>Creates a 32-bit integer value.</summary>
    public static CuteValue Int32(int value) => new(CuteType.Int32, unchecked((ulong)(long)value));

    /// <summary>Creates a 64-bit integer value.</summary>
    public static CuteValue Int64(long value) => new(CuteType.Int64, unchecked((ulong)value));

    /// <summary>Creates a double value.</summary>
    public static CuteValue Double(double value) => new(CuteType.Double, BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Creates a decimal value.</summary>
    public static CuteValue Decimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var lo = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        var hi = ((ulong)(uint)bits[3] << 32) | (uint)bits[2];
        return new CuteValue(CuteType.Decimal, lo, hi);
    }

    /// <summary>Creates a string value. A null string becomes <see cref="Null"/>.</summary>
    public static CuteValue String(string? value)
        => value is null ? Null : new CuteValue(CuteType.String, reference: value);

    /// <summary>Creates a binary value. A null array becomes <see cref="Null"/>.</summary>
    public static CuteValue Binary(byte[]? value)
        => value is null ? Null : new CuteValue(CuteType.Binary, reference: value);

    /// <summary>Creates a UTC instant. Non-UTC input is converted first.</summary>
    public static CuteValue DateTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => System.DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return new CuteValue(CuteType.DateTime, unchecked((ulong)utc.Ticks));
    }

    /// <summary>Creates a GUID value.</summary>
    public static CuteValue Guid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes);
        return new CuteValue(
            CuteType.Guid,
            MemoryMarshal.Read<ulong>(bytes),
            MemoryMarshal.Read<ulong>(bytes[8..]));
    }

    /// <summary>Creates a value holding a document id.</summary>
    public static CuteValue Id(CuteId value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.Write(bytes);
        return new CuteValue(
            CuteType.Id,
            MemoryMarshal.Read<ulong>(bytes),
            MemoryMarshal.Read<ulong>(bytes[8..]));
    }

    /// <summary>Wraps an object.</summary>
    public static CuteValue Object(CuteObject value)
        => new(CuteType.Object, reference: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Wraps an array.</summary>
    public static CuteValue Array(CuteArray value)
        => new(CuteType.Array, reference: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Builds an array from a sequence of values.</summary>
    public static CuteValue ArrayOf(IEnumerable<CuteValue> values) => Array([.. values]);

    /// <summary>Builds an array from a span of values.</summary>
    public static CuteValue ArrayOf(params ReadOnlySpan<CuteValue> values)
    {
        var array = new CuteArray(values.Length);
        foreach (var value in values)
        {
            array.Add(value);
        }

        return Array(array);
    }

    // ---------------------------------------------------------------------------------------
    // Accessors
    // ---------------------------------------------------------------------------------------

    /// <summary>The boolean payload. Throws when this is not a boolean.</summary>
    public bool AsBoolean => _type switch
    {
        CuteType.True => true,
        CuteType.False => false,
        _ => throw TypeError("bool"),
    };

    /// <summary>The string payload. Throws when this is not a string.</summary>
    public string AsString => _type == CuteType.String
        ? Unsafe.As<string>(_reference)!
        : throw TypeError("string");

    /// <summary>The binary payload. Throws when this is not binary.</summary>
    public byte[] AsBinary => _type == CuteType.Binary
        ? Unsafe.As<byte[]>(_reference)!
        : throw TypeError("binary");

    /// <summary>The object payload. Throws when this is not an object.</summary>
    public CuteObject AsObject => _type == CuteType.Object
        ? Unsafe.As<CuteObject>(_reference)!
        : throw TypeError("object");

    /// <summary>The array payload. Throws when this is not an array.</summary>
    public CuteArray AsArray => _type == CuteType.Array
        ? Unsafe.As<CuteArray>(_reference)!
        : throw TypeError("array");

    /// <summary>The instant payload, always UTC. Throws when this is not a datetime.</summary>
    public DateTime AsDateTime => _type == CuteType.DateTime
        ? new DateTime(unchecked((long)_lo), DateTimeKind.Utc)
        : throw TypeError("datetime");

    /// <summary>The GUID payload. Throws when this is not a GUID.</summary>
    public Guid AsGuid
    {
        get
        {
            if (_type != CuteType.Guid)
            {
                throw TypeError("guid");
            }

            Span<byte> bytes = stackalloc byte[16];
            MemoryMarshal.Write(bytes, in _lo);
            MemoryMarshal.Write(bytes[8..], in _hi);
            return new Guid(bytes);
        }
    }

    /// <summary>The document id payload. Throws when this is not an id.</summary>
    public CuteId AsId
    {
        get
        {
            if (_type != CuteType.Id)
            {
                throw TypeError("id");
            }

            Span<byte> bytes = stackalloc byte[16];
            MemoryMarshal.Write(bytes, in _lo);
            MemoryMarshal.Write(bytes[8..], in _hi);
            return CuteId.Read(bytes);
        }
    }

    /// <summary>The decimal payload. Throws when this is not a decimal.</summary>
    public decimal AsDecimal
    {
        get
        {
            if (_type != CuteType.Decimal)
            {
                throw TypeError("decimal");
            }

            return DecimalFromBits(_lo, _hi);
        }
    }

    /// <summary>
    /// This value as a <see cref="double"/>, converting from any numeric type. Throws when this is
    /// not numeric — use <see cref="TryGetDouble"/> to test first.
    /// </summary>
    public double AsDouble => _type switch
    {
        CuteType.Int32 => unchecked((int)(long)_lo),
        CuteType.Int64 => unchecked((long)_lo),
        CuteType.Double => BitConverter.UInt64BitsToDouble(_lo),
        CuteType.Decimal => (double)DecimalFromBits(_lo, _hi),
        _ => throw TypeError("number"),
    };

    /// <summary>
    /// This value as a <see cref="long"/>, converting from any numeric type. Fractional doubles
    /// are truncated toward zero.
    /// </summary>
    public long AsInt64 => _type switch
    {
        CuteType.Int32 => unchecked((int)(long)_lo),
        CuteType.Int64 => unchecked((long)_lo),
        CuteType.Double => (long)BitConverter.UInt64BitsToDouble(_lo),
        CuteType.Decimal => (long)DecimalFromBits(_lo, _hi),
        _ => throw TypeError("number"),
    };

    /// <summary>This value as an <see cref="int"/>, converting from any numeric type.</summary>
    public int AsInt32 => checked((int)AsInt64);

    /// <summary>Reads a numeric value as a double without throwing.</summary>
    public bool TryGetDouble(out double value)
    {
        if (IsNumber)
        {
            value = AsDouble;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Reads a string value without throwing.</summary>
    public bool TryGetString([NotNullWhen(true)] out string? value)
    {
        value = _type == CuteType.String ? Unsafe.As<string>(_reference) : null;
        return value is not null;
    }

    /// <summary>
    /// Interprets this value as a condition the way CuteQL does: false, null, missing, zero, the
    /// empty string and empty containers are falsey; everything else is truthy.
    /// </summary>
    public bool IsTruthy => _type switch
    {
        CuteType.Null or CuteType.Missing or CuteType.False => false,
        CuteType.True => true,
        CuteType.Int32 or CuteType.Int64 => _lo != 0,
        CuteType.Double => BitConverter.UInt64BitsToDouble(_lo) != 0,
        CuteType.Decimal => DecimalFromBits(_lo, _hi) != 0m,
        CuteType.String => Unsafe.As<string>(_reference)!.Length > 0,
        CuteType.Binary => Unsafe.As<byte[]>(_reference)!.Length > 0,
        CuteType.Array => Unsafe.As<CuteArray>(_reference)!.Count > 0,
        CuteType.Object => Unsafe.As<CuteObject>(_reference)!.Count > 0,
        _ => true,
    };

    /// <summary>
    /// Looks up a field on an object. Returns <see cref="Missing"/> for an absent field and for
    /// any non-object receiver, so chained lookups never throw.
    /// </summary>
    public CuteValue this[string key]
        => _type == CuteType.Object && Unsafe.As<CuteObject>(_reference)!.TryGetValue(key, out var value)
            ? value
            : Missing;

    /// <summary>
    /// Indexes into an array. Returns <see cref="Missing"/> for an out-of-range index and for any
    /// non-array receiver. Negative indices count from the end.
    /// </summary>
    public CuteValue this[int index]
    {
        get
        {
            if (_type != CuteType.Array)
            {
                return Missing;
            }

            var array = Unsafe.As<CuteArray>(_reference)!;
            var effective = index < 0 ? array.Count + index : index;
            return (uint)effective < (uint)array.Count ? array[effective] : Missing;
        }
    }

    /// <summary>
    /// The number of entries in a container, the number of characters in a string, or 0 for a
    /// scalar.
    /// </summary>
    public int Count => _type switch
    {
        CuteType.Array => Unsafe.As<CuteArray>(_reference)!.Count,
        CuteType.Object => Unsafe.As<CuteObject>(_reference)!.Count,
        CuteType.String => Unsafe.As<string>(_reference)!.Length,
        CuteType.Binary => Unsafe.As<byte[]>(_reference)!.Length,
        _ => 0,
    };

    // ---------------------------------------------------------------------------------------
    // Implicit conversions, so building documents reads like writing JSON
    // ---------------------------------------------------------------------------------------

    public static implicit operator CuteValue(bool value) => Boolean(value);

    public static implicit operator CuteValue(int value) => Int32(value);

    public static implicit operator CuteValue(long value) => Int64(value);

    public static implicit operator CuteValue(double value) => Double(value);

    public static implicit operator CuteValue(float value) => Double(value);

    public static implicit operator CuteValue(decimal value) => Decimal(value);

    public static implicit operator CuteValue(string? value) => String(value);

    public static implicit operator CuteValue(byte[]? value) => Binary(value);

    public static implicit operator CuteValue(DateTime value) => DateTime(value);

    public static implicit operator CuteValue(DateTimeOffset value) => DateTime(value.UtcDateTime);

    public static implicit operator CuteValue(Guid value) => Guid(value);

    public static implicit operator CuteValue(CuteId value) => Id(value);

    public static implicit operator CuteValue(CuteObject value) => Object(value);

    public static implicit operator CuteValue(CuteArray value) => Array(value);

    // ---------------------------------------------------------------------------------------
    // Equality and ordering
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Value equality. Numbers compare across representations, so <c>Int32(1)</c> equals
    /// <c>Double(1.0)</c>; containers compare element by element.
    /// </summary>
    public bool Equals(CuteValue other) => CuteValueComparer.Equal(this, other);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is CuteValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => CuteValueComparer.GetHashCode(this);

    /// <summary>
    /// Total ordering used by <c>ORDER BY</c>. Values of different types are ordered by type rank
    /// (missing, null, bool, number, string, binary, datetime, guid, id, array, object) so that a
    /// sort over a heterogeneous field is still deterministic.
    /// </summary>
    public int CompareTo(CuteValue other) => CuteValueComparer.Compare(this, other);

    public static bool operator ==(CuteValue left, CuteValue right) => left.Equals(right);

    public static bool operator !=(CuteValue left, CuteValue right) => !left.Equals(right);

    public static bool operator <(CuteValue left, CuteValue right) => left.CompareTo(right) < 0;

    public static bool operator >(CuteValue left, CuteValue right) => left.CompareTo(right) > 0;

    public static bool operator <=(CuteValue left, CuteValue right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CuteValue left, CuteValue right) => left.CompareTo(right) >= 0;

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    /// <summary>Renders this value as compact JSON.</summary>
    public override string ToString() => CuteJson.Write(this, indented: false);

    /// <summary>Renders this value as JSON, optionally indented.</summary>
    public string ToJson(bool indented = false) => CuteJson.Write(this, indented);

    /// <summary>
    /// A short one-line rendering for diagnostics and for the CLI's table cells: strings are
    /// unquoted, containers collapse to a shape summary.
    /// </summary>
    public string ToDisplayString() => _type switch
    {
        CuteType.Missing => string.Empty,
        CuteType.Null => "null",
        CuteType.True => "true",
        CuteType.False => "false",
        CuteType.String => Unsafe.As<string>(_reference)!,
        CuteType.Int32 or CuteType.Int64 => AsInt64.ToString(CultureInfo.InvariantCulture),
        CuteType.Double => AsDouble.ToString("R", CultureInfo.InvariantCulture),
        CuteType.Decimal => AsDecimal.ToString(CultureInfo.InvariantCulture),
        CuteType.DateTime => AsDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        CuteType.Guid => AsGuid.ToString(),
        CuteType.Id => AsId.ToString(),
        CuteType.Binary => $"0x{Convert.ToHexString(AsBinary.AsSpan(0, Math.Min(8, AsBinary.Length)))}{(AsBinary.Length > 8 ? "…" : string.Empty)}",
        CuteType.Array => $"[{Count} items]",
        CuteType.Object => $"{{{Count} fields}}",
        _ => string.Empty,
    };

    internal string ToDebugString() => $"{_type.ToDisplayName()}: {ToDisplayString()}";

    internal static decimal DecimalFromBits(ulong lo, ulong hi)
    {
        Span<int> bits =
        [
            unchecked((int)(uint)lo),
            unchecked((int)(uint)(lo >> 32)),
            unchecked((int)(uint)hi),
            unchecked((int)(uint)(hi >> 32)),
        ];

        return new decimal(bits);
    }

    internal void GetRawBits(out ulong lo, out ulong hi)
    {
        lo = _lo;
        hi = _hi;
    }

    private InvalidOperationException TypeError(string expected)
        => new($"Expected a {expected} but the value is {_type.ToDisplayName()}.");
}
