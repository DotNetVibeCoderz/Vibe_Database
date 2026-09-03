using System.Globalization;

namespace MemSharp.Protocol;

/// <summary>The RESP wire types MemSharp produces.</summary>
public enum RespKind
{
    /// <summary>A status reply such as <c>+OK</c>.</summary>
    SimpleString,
    /// <summary>An error reply, carrying a code and a message.</summary>
    Error,
    /// <summary>A 64-bit integer.</summary>
    Integer,
    /// <summary>A binary-safe string, or the null bulk string.</summary>
    BulkString,
    /// <summary>An ordered sequence of replies, or the null array.</summary>
    Array,
    /// <summary>A double, sent as a bulk string for RESP2 compatibility.</summary>
    Double,
}

/// <summary>
/// One RESP reply.
/// </summary>
/// <remarks>
/// A class rather than a struct despite being small and immutable: replies nest, and an array reply
/// holding a <c>RespValue[]</c> of structs would copy every element into the array on construction
/// and again on every read. Nesting is the common case for anything that returns a list.
/// </remarks>
public sealed class RespValue
{
    private RespValue(RespKind kind, string? text, long integer, double number, RespValue[]? items)
    {
        Kind = kind;
        Text = text;
        Integer = integer;
        Number = number;
        Items = items;
    }

    /// <summary>Which RESP type this is.</summary>
    public RespKind Kind { get; }

    /// <summary>The payload for a string or error reply; <c>null</c> for a null bulk string.</summary>
    public string? Text { get; }

    /// <summary>The payload for an integer reply.</summary>
    public long Integer { get; }

    /// <summary>The payload for a double reply.</summary>
    public double Number { get; }

    /// <summary>The elements of an array reply; <c>null</c> for a null array.</summary>
    public RespValue[]? Items { get; }

    /// <summary>True if this is a null bulk string or a null array.</summary>
    public bool IsNull => (Kind == RespKind.BulkString && Text is null) || (Kind == RespKind.Array && Items is null);

    /// <summary><c>+OK</c>.</summary>
    public static RespValue Ok { get; } = new(RespKind.SimpleString, "OK", 0, 0, null);

    /// <summary>The null bulk string, <c>$-1</c>.</summary>
    public static RespValue Null { get; } = new(RespKind.BulkString, null, 0, 0, null);

    /// <summary>The empty array.</summary>
    public static RespValue EmptyArray { get; } = new(RespKind.Array, null, 0, 0, []);

    /// <summary>A status reply.</summary>
    public static RespValue Status(string text) => new(RespKind.SimpleString, text, 0, 0, null);

    /// <summary>An error reply. <paramref name="code"/> is the leading token clients switch on.</summary>
    public static RespValue Error(string code, string message) => new(RespKind.Error, $"{code} {message}", 0, 0, null);

    /// <summary>An integer reply.</summary>
    public static RespValue Number64(long value) => new(RespKind.Integer, null, value, 0, null);

    /// <summary>A boolean, as the integer 1 or 0 - RESP2 has no boolean type.</summary>
    public static RespValue Boolean(bool value) => Number64(value ? 1 : 0);

    /// <summary>A bulk string, or the null bulk string when <paramref name="text"/> is <c>null</c>.</summary>
    public static RespValue Bulk(string? text) => text is null ? Null : new(RespKind.BulkString, text, 0, 0, null);

    /// <summary>A double, sent as a bulk string.</summary>
    public static RespValue Float(double value) => new(RespKind.Double, null, 0, value, null);

    /// <summary>An array reply.</summary>
    public static RespValue Array(params RespValue[] items) => new(RespKind.Array, null, 0, 0, items);

    /// <summary>An array of bulk strings.</summary>
    public static RespValue BulkArray(IEnumerable<string?> values)
    {
        var items = new List<RespValue>();
        foreach (var value in values) items.Add(Bulk(value));
        return Array(items.ToArray());
    }

    /// <summary>Renders the reply as human-readable text, for the CLI and for tests.</summary>
    public string ToDisplayString() => Kind switch
    {
        RespKind.SimpleString => Text ?? string.Empty,
        RespKind.Error => Text ?? "ERR",
        RespKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        RespKind.Double => Number.ToString("R", CultureInfo.InvariantCulture),
        RespKind.BulkString => Text ?? "(nil)",
        RespKind.Array when Items is null => "(nil)",
        RespKind.Array when Items.Length == 0 => "(empty)",
        RespKind.Array => string.Join(", ", Items.Select(item => item.ToDisplayString())),
        _ => string.Empty,
    };

    /// <inheritdoc />
    public override string ToString() => ToDisplayString();
}
