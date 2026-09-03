namespace CuteDB;

/// <summary>How a CLR property name becomes a document field name.</summary>
public enum CuteNamingPolicy
{
    /// <summary>
    /// <c>PlacedAt</c> becomes <c>placedAt</c>. The default, because documents are JSON-shaped and
    /// this is what every JSON producer on the other side of the wire will send.
    /// </summary>
    CamelCase = 0,

    /// <summary>The property name verbatim: <c>PlacedAt</c> stays <c>PlacedAt</c>.</summary>
    Exact = 1,

    /// <summary><c>PlacedAt</c> becomes <c>placed_at</c>.</summary>
    SnakeCase = 2,
}

/// <summary>
/// Gives a property a specific document field name, overriding the naming policy.
/// </summary>
/// <example>
/// <code>
/// public sealed class Order
/// {
///     [CuteField("code")] public string OrderCode { get; set; } = "";
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class CuteFieldAttribute(string name) : Attribute
{
    /// <summary>The field name to read and write.</summary>
    public string Name { get; } = name;
}

/// <summary>Leaves a property out of the document entirely.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class CuteIgnoreAttribute : Attribute;

/// <summary>
/// Marks the property that carries the document's primary key.
/// </summary>
/// <remarks>
/// Optional. A property called <c>Id</c> of type <see cref="CuteId"/> or <see cref="string"/> is
/// treated as the key without any attribute; this is for when it is called something else.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class CuteIdAttribute : Attribute;

/// <summary>Sets the naming policy for one type, overriding the mapper's default.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class CuteNamingAttribute(CuteNamingPolicy policy) : Attribute
{
    /// <summary>The policy to apply to this type's properties.</summary>
    public CuteNamingPolicy Policy { get; } = policy;
}
