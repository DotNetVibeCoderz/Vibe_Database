using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace CuteDB.Mapping;

/// <summary>One mapped property: its document field name, and compiled access to it.</summary>
internal sealed class CuteMember
{
    internal required string FieldName { get; init; }

    internal required string ClrName { get; init; }

    internal required Type Type { get; init; }

    internal required bool IsKey { get; init; }

    /// <summary>Reads the property from an instance. Compiled, not reflected, per call.</summary>
    internal required Func<object, object?> Get { get; init; }

    /// <summary>Writes the property. Null for a get-only property.</summary>
    internal Action<object, object?>? Set { get; init; }
}

/// <summary>
/// The mapping between a CLR type and a document shape, worked out once per type.
/// </summary>
/// <remarks>
/// <para>
/// Building this involves reflection, attribute lookups and expression compilation, all of which
/// are far too slow to repeat per document. It is computed on first use and cached for the life of
/// the process, after which reading a property is a delegate call.
/// </para>
/// <para>
/// The naming policy defaults to camelCase because documents are JSON-shaped: a .NET
/// <c>PlacedAt</c> is a <c>placedAt</c> on the wire, in the sample data, and in whatever the
/// Python or Node client sends. <c>[CuteField]</c> overrides it per property and
/// <c>[CuteNaming]</c> per type.
/// </para>
/// </remarks>
internal sealed class CuteTypeMap
{
    private static readonly ConcurrentDictionary<(Type Type, CuteNamingPolicy Policy), CuteTypeMap> Cache = new();

    private readonly Dictionary<string, CuteMember> _byFieldName;
    private readonly Dictionary<string, CuteMember> _byClrName;

    private CuteTypeMap(Type type, CuteNamingPolicy policy, List<CuteMember> members)
    {
        Type = type;
        Policy = policy;
        Members = members;
        Key = members.FirstOrDefault(m => m.IsKey);

        _byFieldName = members.ToDictionary(m => m.FieldName, StringComparer.Ordinal);
        _byClrName = members.ToDictionary(m => m.ClrName, StringComparer.Ordinal);

        // A parameterless constructor is the ordinary case; a record with only a primary
        // constructor is not, and is reported when someone actually tries to materialise one.
        Constructor = type.GetConstructor(Type.EmptyTypes);
    }

    internal Type Type { get; }

    internal CuteNamingPolicy Policy { get; }

    internal IReadOnlyList<CuteMember> Members { get; }

    /// <summary>The member carrying the document id, if the type has one.</summary>
    internal CuteMember? Key { get; }

    internal ConstructorInfo? Constructor { get; }

    /// <summary>Gets the map for a type, building it on first use.</summary>
    internal static CuteTypeMap For(Type type, CuteNamingPolicy policy)
        => Cache.GetOrAdd((type, policy), static key => Build(key.Type, key.Policy));

    /// <summary>Finds a member by its document field name.</summary>
    internal CuteMember? ByField(string fieldName) => _byFieldName.GetValueOrDefault(fieldName);

    /// <summary>Finds a member by its CLR property name — what a LINQ expression tree carries.</summary>
    internal CuteMember? ByClrName(string clrName) => _byClrName.GetValueOrDefault(clrName);

    private static CuteTypeMap Build(Type type, CuteNamingPolicy defaultPolicy)
    {
        var policy = type.GetCustomAttribute<CuteNamingAttribute>()?.Policy ?? defaultPolicy;
        var members = new List<CuteMember>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<CuteIgnoreAttribute>() is not null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (!property.CanRead)
            {
                continue;
            }

            members.Add(new CuteMember
            {
                FieldName = FieldNameFor(property, policy),
                ClrName = property.Name,
                Type = property.PropertyType,
                IsKey = IsKey(property),
                Get = CompileGetter(type, property),
                Set = property.CanWrite ? CompileSetter(type, property) : null,
            });
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<CuteIgnoreAttribute>() is not null)
            {
                continue;
            }

            members.Add(new CuteMember
            {
                FieldName = FieldNameFor(field, policy),
                ClrName = field.Name,
                Type = field.FieldType,
                IsKey = IsKey(field),
                Get = CompileGetter(type, field),
                Set = CompileSetter(type, field),
            });
        }

        return new CuteTypeMap(type, policy, members);
    }

    private static bool IsKey(MemberInfo member)
    {
        if (member.GetCustomAttribute<CuteIdAttribute>() is not null)
        {
            return true;
        }

        // Convention: a member called Id or _id of a type that can hold one.
        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        return (member.Name is "Id" or "_id" or "ID")
            && (type == typeof(CuteId) || type == typeof(string) || type == typeof(CuteId?));
    }

    private static string FieldNameFor(MemberInfo member, CuteNamingPolicy policy)
    {
        if (member.GetCustomAttribute<CuteFieldAttribute>() is { } field)
        {
            return field.Name;
        }

        // The key always lands on the reserved _id field, whatever the property is called.
        if (IsKey(member))
        {
            return CuteDocument.IdField;
        }

        return Apply(member.Name, policy);
    }

    /// <summary>Applies a naming policy to a CLR name.</summary>
    internal static string Apply(string name, CuteNamingPolicy policy)
    {
        if (name.Length == 0)
        {
            return name;
        }

        switch (policy)
        {
            case CuteNamingPolicy.Exact:
                return name;

            case CuteNamingPolicy.SnakeCase:
            {
                var builder = new StringBuilder(name.Length + 8);
                for (var i = 0; i < name.Length; i++)
                {
                    var c = name[i];
                    if (char.IsUpper(c))
                    {
                        if (i > 0)
                        {
                            builder.Append('_');
                        }

                        builder.Append(char.ToLowerInvariant(c));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                return builder.ToString();
            }

            default:
            {
                if (!char.IsUpper(name[0]))
                {
                    return name;
                }

                // A run of capitals is an acronym: HTTPStatus becomes httpStatus, not hTTPStatus.
                var upper = 0;
                while (upper < name.Length && char.IsUpper(name[upper]))
                {
                    upper++;
                }

                if (upper > 1 && upper < name.Length)
                {
                    upper--;
                }

                return string.Concat(name.AsSpan(0, upper).ToString().ToLowerInvariant(), name.AsSpan(upper));
            }
        }
    }

    /// <summary>
    /// Compiles a property or field read into a delegate.
    /// </summary>
    /// <remarks>
    /// <c>PropertyInfo.GetValue</c> costs a few hundred nanoseconds through reflection; a compiled
    /// delegate costs a virtual call. Mapping ten thousand documents makes that the difference
    /// between milliseconds and seconds.
    /// </remarks>
    private static Func<object, object?> CompileGetter(Type declaring, MemberInfo member)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var typed = Expression.Convert(instance, declaring);
        var access = Expression.MakeMemberAccess(typed, member);
        var boxed = Expression.Convert(access, typeof(object));

        return Expression.Lambda<Func<object, object?>>(boxed, instance).Compile();
    }

    private static Action<object, object?> CompileSetter(Type declaring, MemberInfo member)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");

        var memberType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        var typed = Expression.Convert(instance, declaring);
        var access = Expression.MakeMemberAccess(typed, member);
        var assign = Expression.Assign(access, Expression.Convert(value, memberType));

        return Expression.Lambda<Action<object, object?>>(assign, instance, value).Compile();
    }
}
