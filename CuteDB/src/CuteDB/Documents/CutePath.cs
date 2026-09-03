using System.Diagnostics;
using System.Text;

namespace CuteDB;

/// <summary>
/// A compiled field path such as <c>customer.address.city</c>, <c>lines[0].sku</c> or
/// <c>lines[].sku</c>.
/// </summary>
/// <remarks>
/// <para>
/// Paths are parsed once and reused for every document a query touches, which matters because a
/// scan resolves the same path millions of times. The parsed form is also what gets handed to the
/// native accelerator, so the path text is never re-parsed per row.
/// </para>
/// <para>
/// The <c>[]</c> segment is the interesting one: it projects across an array rather than indexing
/// into it, so <c>lines[].sku</c> against a document with three order lines resolves to an array
/// of three SKUs. That is what makes <c>WHERE lines[].sku = 'X'</c> mean "any line has that SKU"
/// without a join.
/// </para>
/// </remarks>
[DebuggerDisplay("{Text,nq}")]
public sealed class CutePath : IEquatable<CutePath>
{
    private readonly Segment[] _segments;

    private CutePath(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The path as written.</summary>
    public string Text { get; }

    /// <summary>The number of segments.</summary>
    public int Length => _segments.Length;

    /// <summary>True when the path is a single field name with no nesting or indexing.</summary>
    public bool IsSimpleField => _segments.Length == 1 && _segments[0].Kind == SegmentKind.Field;

    /// <summary>
    /// True when the path contains a <c>[]</c> segment, which projects across an array rather than
    /// indexing into it.
    /// </summary>
    /// <remarks>
    /// The native accelerator refuses these: reproducing a projection would mean building an array
    /// per row to compare against, and a scan that allocates per row is not worth accelerating.
    /// The managed evaluator handles them, so a projecting path costs speed and nothing else.
    /// </remarks>
    public bool HasProjection
    {
        get
        {
            foreach (var segment in _segments)
            {
                if (segment.Kind == SegmentKind.Projection)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The path's first field name, or null when it does not start with one.</summary>
    public string? RootField => _segments.Length > 0 && _segments[0].Kind == SegmentKind.Field
        ? _segments[0].Name
        : null;

    /// <summary>Parses a path, throwing on malformed input.</summary>
    public static CutePath Parse(string path)
        => TryParse(path, out var parsed, out var error)
            ? parsed
            : throw new CuteDbException($"Invalid field path '{path}': {error}");

    /// <summary>Parses a path, reporting the problem instead of throwing.</summary>
    public static bool TryParse(string path, out CutePath result, out string? error)
    {
        result = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "the path is empty.";
            return false;
        }

        var segments = new List<Segment>(4);
        var index = 0;
        var span = path.AsSpan();

        while (index < span.Length)
        {
            if (span[index] == '[')
            {
                var close = span[index..].IndexOf(']');
                if (close < 0)
                {
                    error = "an opening '[' has no matching ']'.";
                    return false;
                }

                var inner = span.Slice(index + 1, close - 1).Trim();
                if (inner.IsEmpty)
                {
                    segments.Add(Segment.Projection());
                }
                else if (int.TryParse(inner, out var arrayIndex))
                {
                    segments.Add(Segment.AtIndex(arrayIndex));
                }
                else
                {
                    error = $"'[{inner}]' is not an array index. Use [0], [-1] or [] to project.";
                    return false;
                }

                index += close + 1;

                // A '.' after a bracket is optional: lines[0].sku and lines[0]sku both parse, but
                // only the first is worth writing.
                if (index < span.Length && span[index] == '.')
                {
                    index++;
                }

                continue;
            }

            var end = index;
            while (end < span.Length && span[end] != '.' && span[end] != '[')
            {
                end++;
            }

            if (end == index)
            {
                error = $"empty field name at position {index}.";
                return false;
            }

            segments.Add(Segment.Field(span[index..end].ToString()));
            index = end;

            if (index < span.Length && span[index] == '.')
            {
                index++;
                if (index == span.Length)
                {
                    error = "the path ends with a '.'.";
                    return false;
                }
            }
        }

        result = new CutePath(path, [.. segments]);
        return true;
    }

    /// <summary>Resolves the path against a value, yielding <see cref="CuteValue.Missing"/> when it does not match.</summary>
    public CuteValue Resolve(CuteValue root)
    {
        var current = root;
        for (var i = 0; i < _segments.Length; i++)
        {
            ref readonly var segment = ref _segments[i];
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    current = current[segment.Name!];
                    break;

                case SegmentKind.Index:
                    current = current[segment.Index];
                    break;

                case SegmentKind.Projection:
                    return ResolveProjection(current, i + 1);
            }

            if (current.IsMissing)
            {
                return CuteValue.Missing;
            }
        }

        return current;
    }

    /// <summary>
    /// Resolves the path directly against an encoded document, decoding only the value the path
    /// lands on.
    /// </summary>
    /// <remarks>
    /// This is the hot path for a filtering scan. Walking the encoded bytes lets the reader jump
    /// over whole subtrees using their length prefix, so testing <c>customer.city</c> across a
    /// million documents never materialises the rest of any of them. The managed scanner and the
    /// Rust accelerator both work this way, and must agree exactly.
    /// </remarks>
    public CuteValue ResolveEncoded(ReadOnlySpan<byte> encoded)
    {
        var current = encoded;
        for (var i = 0; i < _segments.Length; i++)
        {
            ref readonly var segment = ref _segments[i];
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    if (!CuteBinary.TryGetField(current, segment.NameUtf8!, out current))
                    {
                        return CuteValue.Missing;
                    }

                    break;

                case SegmentKind.Index:
                    if (!CuteBinary.TryGetElement(current, segment.Index, out current))
                    {
                        return CuteValue.Missing;
                    }

                    break;

                case SegmentKind.Projection:
                    return ResolveEncodedProjection(current, i + 1);
            }
        }

        return CuteBinary.Decode(current);
    }

    private CuteValue ResolveEncodedProjection(ReadOnlySpan<byte> current, int nextSegment)
    {
        var length = CuteBinary.GetArrayLength(current);
        if (length < 0)
        {
            return CuteValue.Missing;
        }

        var projected = new CuteArray(length);
        var tail = nextSegment >= _segments.Length ? null : new CutePath(Text, _segments[nextSegment..]);

        foreach (var element in CuteBinary.EnumerateArray(current))
        {
            var resolved = tail is null ? CuteBinary.Decode(element) : tail.ResolveEncoded(element);
            if (!resolved.IsMissing)
            {
                projected.Add(resolved);
            }
        }

        return CuteValue.Array(projected);
    }

    /// <summary>
    /// Writes a value at this path, creating intermediate objects as needed. Projection segments
    /// are not supported for writes.
    /// </summary>
    public void Assign(CuteObject root, CuteValue value)
    {
        var current = CuteValue.Object(root);
        for (var i = 0; i < _segments.Length - 1; i++)
        {
            ref readonly var segment = ref _segments[i];
            if (segment.Kind == SegmentKind.Projection)
            {
                throw new CuteDbException($"Cannot assign through a projecting path: '{Text}'.");
            }

            var next = segment.Kind == SegmentKind.Field ? current[segment.Name!] : current[segment.Index];
            if (!next.IsObject && !next.IsArray)
            {
                // The next segment decides what the missing container has to be.
                next = _segments[i + 1].Kind == SegmentKind.Field ? CuteValue.EmptyObject : CuteValue.EmptyArray;
                StoreInto(current, in segment, next);
            }

            current = next;
        }

        StoreInto(current, in _segments[^1], value);
    }

    /// <summary>Encodes the parsed path in the wire form the native accelerator consumes.</summary>
    internal void Encode(CuteBufferWriter writer)
    {
        writer.WriteVarUInt((uint)_segments.Length);
        foreach (var segment in _segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    writer.WriteByte(0);
                    writer.WriteVarUInt((uint)segment.NameUtf8!.Length);
                    writer.WriteBytes(segment.NameUtf8);
                    break;

                case SegmentKind.Index:
                    writer.WriteByte(1);
                    writer.WriteVarUInt(unchecked((uint)segment.Index));
                    break;

                case SegmentKind.Projection:
                    writer.WriteByte(2);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(CutePath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CutePath);

    /// <inheritdoc />
    public override int GetHashCode() => string.GetHashCode(Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Text;

    public static implicit operator CutePath(string path) => Parse(path);

    private CuteValue ResolveProjection(CuteValue current, int nextSegment)
    {
        if (!current.IsArray)
        {
            return CuteValue.Missing;
        }

        var source = current.AsArray;
        var projected = new CuteArray(source.Count);
        var tail = nextSegment >= _segments.Length ? null : new CutePath(Text, _segments[nextSegment..]);

        foreach (var item in source.AsSpan())
        {
            var resolved = tail is null ? item : tail.Resolve(item);
            if (!resolved.IsMissing)
            {
                projected.Add(resolved);
            }
        }

        return CuteValue.Array(projected);
    }

    private static void StoreInto(CuteValue container, ref readonly Segment segment, CuteValue value)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Field when container.IsObject:
                container.AsObject.Set(segment.Name!, value);
                break;

            case SegmentKind.Index when container.IsArray:
            {
                var array = container.AsArray;
                var index = segment.Index < 0 ? array.Count + segment.Index : segment.Index;
                while (array.Count <= index)
                {
                    array.Add(CuteValue.Null);
                }

                array[index] = value;
                break;
            }

            default:
                throw new CuteDbException(
                    $"Cannot write a {(segment.Kind == SegmentKind.Field ? "field" : "index")} into a {container.Type.ToDisplayName()}.");
        }
    }

    private enum SegmentKind : byte
    {
        Field = 0,
        Index = 1,
        Projection = 2,
    }

    private readonly struct Segment
    {
        internal readonly SegmentKind Kind;
        internal readonly string? Name;

        /// <summary>
        /// The field name pre-encoded as UTF-8. Resolving a path against an encoded document
        /// compares raw key bytes, and doing that conversion once per path instead of once per
        /// document per segment is most of the reason an encoded scan is worth having.
        /// </summary>
        internal readonly byte[]? NameUtf8;
        internal readonly int Index;

        private Segment(SegmentKind kind, string? name, int index)
        {
            Kind = kind;
            Name = name;
            NameUtf8 = name is null ? null : Encoding.UTF8.GetBytes(name);
            Index = index;
        }

        internal static Segment Field(string name) => new(SegmentKind.Field, name, 0);

        internal static Segment AtIndex(int index) => new(SegmentKind.Index, null, index);

        internal static Segment Projection() => new(SegmentKind.Projection, null, 0);
    }
}
