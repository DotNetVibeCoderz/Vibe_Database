using System.Collections;
using System.Diagnostics;

namespace CuteDB;

/// <summary>
/// A stored document: a <see cref="CuteObject"/> that carries a <see cref="CuteId"/> under the
/// reserved <c>_id</c> field.
/// </summary>
/// <remarks>
/// The id lives inside the document rather than only in the collection's slot table, so an
/// exported document, a document handed to a client SDK and a document read back off disk all
/// identify themselves without extra context. The collection keeps its own copy of the id in the
/// slot table as well, because that copy is what the lookup index and the scanner read, and going
/// through the encoded body for every row would cost a parse per document.
/// </remarks>
[DebuggerDisplay("{Id} ({Root.Count} fields)")]
public sealed class CuteDocument : IEnumerable<KeyValuePair<string, CuteValue>>
{
    /// <summary>The reserved field name holding a document's primary key.</summary>
    public const string IdField = "_id";

    /// <summary>Wraps an existing object, assigning a fresh id when it has none.</summary>
    public CuteDocument(CuteObject root, bool assignId = true)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));

        if (assignId && !TryReadId(root, out _))
        {
            root.Set(IdField, CuteValue.Id(CuteId.NewId()));
        }
    }

    /// <summary>Creates an empty document with a fresh id.</summary>
    public CuteDocument()
        : this(new CuteObject(), assignId: true)
    {
    }

    /// <summary>The document body, id field included.</summary>
    public CuteObject Root { get; }

    /// <summary>
    /// The document's primary key, or <see cref="CuteId.Empty"/> when the <c>_id</c> field is
    /// absent or holds something other than an id.
    /// </summary>
    public CuteId Id => TryReadId(Root, out var id) ? id : CuteId.Empty;

    /// <summary>The number of fields, including <c>_id</c>.</summary>
    public int Count => Root.Count;

    /// <summary>Reads or writes a top-level field.</summary>
    public CuteValue this[string key]
    {
        get => Root[key];
        set => Root[key] = value;
    }

    /// <summary>Resolves a dotted path such as <c>customer.address.city</c> or <c>lines[0].sku</c>.</summary>
    public CuteValue this[CutePath path] => path.Resolve(CuteValue.Object(Root));

    /// <summary>Sets a field and returns this document, so calls chain.</summary>
    public CuteDocument Set(string key, CuteValue value)
    {
        Root.Set(key, value);
        return this;
    }

    /// <summary>Builds a document from field pairs, assigning a fresh id.</summary>
    public static CuteDocument From(params ReadOnlySpan<KeyValuePair<string, CuteValue>> fields)
    {
        var root = new CuteObject(fields.Length + 1);
        foreach (var (key, value) in fields)
        {
            root.Set(key, value);
        }

        return new CuteDocument(root);
    }

    /// <summary>Parses a JSON object into a document.</summary>
    public static CuteDocument Parse(string json)
    {
        var value = CuteJson.Parse(json);
        return value.IsObject
            ? new CuteDocument(value.AsObject)
            : throw new CuteDbException($"A document must be a JSON object, but the input parsed as {value.Type.ToDisplayName()}.");
    }

    /// <summary>Renders the document as JSON.</summary>
    public string ToJson(bool indented = false) => CuteJson.Write(CuteValue.Object(Root), indented);

    /// <summary>Returns a deep copy, id included.</summary>
    public CuteDocument DeepClone() => new(Root.DeepClone(), assignId: false);

    /// <summary>The document as a plain value, for APIs that take any <see cref="CuteValue"/>.</summary>
    public CuteValue AsValue() => CuteValue.Object(Root);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, CuteValue>> GetEnumerator() => Root.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => ToJson();

    public static implicit operator CuteValue(CuteDocument document) => document.AsValue();

    public static implicit operator CuteObject(CuteDocument document) => document.Root;

    internal static bool TryReadId(CuteObject root, out CuteId id)
    {
        if (root.TryGetValue(IdField, out var value) && value.Type == CuteType.Id)
        {
            id = value.AsId;
            return true;
        }

        // Documents that arrive from JSON, an SDK or an import carry their id as the 24-character
        // hex string, because JSON has no id type. Accept that spelling too.
        if (value.Type == CuteType.String && CuteId.TryParse(value.AsString, out id))
        {
            return true;
        }

        id = CuteId.Empty;
        return false;
    }
}
