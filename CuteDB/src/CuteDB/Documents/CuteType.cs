namespace CuteDB;

/// <summary>
/// The type of a <see cref="CuteValue"/>.
/// </summary>
/// <remarks>
/// The numeric values are part of CuteDB's on-disk format: they are written verbatim as the tag
/// byte of every encoded value, and the Rust accelerator in <c>native/cutedb-core</c> hard-codes
/// the same numbers. Never renumber an existing member — append instead, and bump
/// <see cref="Storage.CuteFileFormat.Version"/> if old files cannot read the new tag.
/// </remarks>
public enum CuteType : byte
{
    /// <summary>An explicit JSON <c>null</c>.</summary>
    Null = 0x00,

    /// <summary><c>false</c>.</summary>
    False = 0x01,

    /// <summary><c>true</c>.</summary>
    True = 0x02,

    /// <summary>A 32-bit signed integer.</summary>
    Int32 = 0x03,

    /// <summary>A 64-bit signed integer.</summary>
    Int64 = 0x04,

    /// <summary>An IEEE-754 double.</summary>
    Double = 0x05,

    /// <summary>A UTF-8 string.</summary>
    String = 0x06,

    /// <summary>An opaque byte string.</summary>
    Binary = 0x07,

    /// <summary>An ordered list of values.</summary>
    Array = 0x08,

    /// <summary>An ordered map of string keys to values.</summary>
    Object = 0x09,

    /// <summary>A UTC instant, stored as 64-bit tick count.</summary>
    DateTime = 0x0A,

    /// <summary>A 16-byte GUID.</summary>
    Guid = 0x0B,

    /// <summary>A .NET <see cref="decimal"/>, stored as its four 32-bit components.</summary>
    Decimal = 0x0C,

    /// <summary>A <see cref="CuteId"/>.</summary>
    Id = 0x0D,

    /// <summary>
    /// The field was not present at all. Never serialized — this is what a path lookup returns
    /// when nothing matched, and it is deliberately distinct from <see cref="Null"/> so that
    /// <c>WHERE x IS NULL</c> can tell "explicitly null" from "absent".
    /// </summary>
    Missing = 0xFF,
}

/// <summary>Convenience predicates over <see cref="CuteType"/>.</summary>
public static class CuteTypeExtensions
{
    /// <summary>True for the four numeric types.</summary>
    public static bool IsNumeric(this CuteType type)
        => type is CuteType.Int32 or CuteType.Int64 or CuteType.Double or CuteType.Decimal;

    /// <summary>True for <see cref="CuteType.True"/> and <see cref="CuteType.False"/>.</summary>
    public static bool IsBoolean(this CuteType type) => type is CuteType.True or CuteType.False;

    /// <summary>True for the two container types.</summary>
    public static bool IsContainer(this CuteType type) => type is CuteType.Array or CuteType.Object;

    /// <summary>The name CuteQL and the CLI use when reporting this type to a human.</summary>
    public static string ToDisplayName(this CuteType type) => type switch
    {
        CuteType.Null => "null",
        CuteType.False or CuteType.True => "bool",
        CuteType.Int32 => "int",
        CuteType.Int64 => "long",
        CuteType.Double => "double",
        CuteType.String => "string",
        CuteType.Binary => "binary",
        CuteType.Array => "array",
        CuteType.Object => "object",
        CuteType.DateTime => "datetime",
        CuteType.Guid => "guid",
        CuteType.Decimal => "decimal",
        CuteType.Id => "id",
        CuteType.Missing => "missing",
        _ => "unknown",
    };
}
