using System.Collections;
using System.Globalization;
using CuteDB.Mapping;

namespace CuteDB;

/// <summary>
/// Converts between CLR objects and CuteDB documents.
/// </summary>
/// <remarks>
/// <para>
/// CuteDB stores schemaless documents, so mapping is a convenience rather than a requirement —
/// <see cref="CuteDocument"/> is always available for shapes no type describes. The mapper exists
/// so that code with a known shape can use it, and so LINQ has something to translate against.
/// </para>
/// <para>
/// Property names become field names through <see cref="CuteNamingPolicy"/>, camelCase by default
/// because documents are JSON-shaped. Nested objects map recursively, collections map to arrays,
/// and the ten types JSON cannot spell — <see cref="decimal"/>, <see cref="DateTime"/>,
/// <see cref="Guid"/>, <see cref="CuteId"/> — round-trip as themselves rather than as strings.
/// </para>
/// </remarks>
public static class CuteMapper
{
    /// <summary>The naming policy used when none is given. camelCase.</summary>
    public static CuteNamingPolicy DefaultNaming { get; set; } = CuteNamingPolicy.CamelCase;

    /// <summary>Converts an object into a document.</summary>
    public static CuteDocument ToDocument<T>(T value, CuteNamingPolicy? naming = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        var converted = ToValue(value, typeof(T) == typeof(object) ? value.GetType() : typeof(T), naming ?? DefaultNaming);
        return converted.IsObject
            ? new CuteDocument(converted.AsObject, assignId: false)
            : throw new CuteDbException($"{typeof(T).Name} does not map to a document; it maps to {converted.Type.ToDisplayName()}.");
    }

    /// <summary>Converts a document into an object of the given type.</summary>
    public static T ToObject<T>(CuteDocument document, CuteNamingPolicy? naming = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (T)FromValue(document.AsValue(), typeof(T), naming ?? DefaultNaming)!;
    }

    /// <summary>Converts a value into an object of the given type.</summary>
    public static T ToObject<T>(CuteValue value, CuteNamingPolicy? naming = null)
        => (T)FromValue(value, typeof(T), naming ?? DefaultNaming)!;

    /// <summary>The document field name a CLR property maps to.</summary>
    public static string FieldNameFor<T>(string propertyName, CuteNamingPolicy? naming = null)
    {
        var map = CuteTypeMap.For(typeof(T), naming ?? DefaultNaming);
        return map.ByClrName(propertyName)?.FieldName
            ?? CuteTypeMap.Apply(propertyName, naming ?? DefaultNaming);
    }

    // -------------------------------------------------------------------------------------
    // CLR -> CuteValue
    // -------------------------------------------------------------------------------------

    internal static CuteValue ToValue(object? value, Type declaredType, CuteNamingPolicy naming)
    {
        if (value is null)
        {
            return CuteValue.Null;
        }

        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(object))
        {
            type = value.GetType();
        }

        switch (value)
        {
            case CuteValue already:
                return already;
            case CuteDocument document:
                return document.AsValue();
            case CuteObject obj:
                return CuteValue.Object(obj);
            case CuteArray array:
                return CuteValue.Array(array);

            case string text:
                return CuteValue.String(text);
            case bool flag:
                return CuteValue.Boolean(flag);
            case byte[] binary:
                return CuteValue.Binary(binary);

            case sbyte or byte or short or ushort or int:
                return CuteValue.Int32(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            case uint or long:
                return CuteValue.Int64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong big:
                // Above long.MaxValue there is no lossless integer type, so it widens to double
                // rather than silently wrapping.
                return big <= long.MaxValue ? CuteValue.Int64((long)big) : CuteValue.Double(big);
            case float or double:
                return CuteValue.Double(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case decimal money:
                return CuteValue.Decimal(money);

            case DateTime moment:
                return CuteValue.DateTime(moment);
            case DateTimeOffset offset:
                return CuteValue.DateTime(offset.UtcDateTime);
            case DateOnly date:
                return CuteValue.DateTime(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            case TimeSpan span:
                return CuteValue.Int64(span.Ticks);
            case Guid guid:
                return CuteValue.Guid(guid);
            case CuteId id:
                return CuteValue.Id(id);
        }

        if (type.IsEnum)
        {
            // Enums store as their name. A stored number would break the moment someone reorders
            // the enum, and a name reads correctly in a query and in an export.
            return CuteValue.String(value.ToString()!);
        }

        if (value is IDictionary dictionary)
        {
            var result = new CuteObject(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                result.Set(
                    entry.Key?.ToString() ?? string.Empty,
                    ToValue(entry.Value, entry.Value?.GetType() ?? typeof(object), naming));
            }

            return CuteValue.Object(result);
        }

        if (value is IEnumerable sequence)
        {
            var element = ElementTypeOf(type) ?? typeof(object);
            var array = new CuteArray();
            foreach (var item in sequence)
            {
                array.Add(ToValue(item, element, naming));
            }

            return CuteValue.Array(array);
        }

        var map = CuteTypeMap.For(type, naming);
        var mapped = new CuteObject(map.Members.Count);

        foreach (var member in map.Members)
        {
            var memberValue = member.Get(value);

            // A null property is written as an explicit null rather than left out, because the
            // two are different questions in CuteQL and the type says the field exists.
            mapped.Set(member.FieldName, ToValue(memberValue, member.Type, naming));
        }

        return CuteValue.Object(mapped);
    }

    // -------------------------------------------------------------------------------------
    // CuteValue -> CLR
    // -------------------------------------------------------------------------------------

    internal static object? FromValue(CuteValue value, Type target, CuteNamingPolicy naming)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying is not null)
        {
            return value.IsNullOrMissing ? null : FromValue(value, underlying, naming);
        }

        if (target == typeof(CuteValue))
        {
            return value;
        }

        if (value.IsNullOrMissing)
        {
            return target.IsValueType ? Activator.CreateInstance(target) : null;
        }

        if (target == typeof(object))
        {
            return ToClrObject(value);
        }

        if (target == typeof(CuteDocument))
        {
            return value.IsObject ? new CuteDocument(value.AsObject, assignId: false) : null;
        }

        if (target == typeof(CuteObject))
        {
            return value.IsObject ? value.AsObject : null;
        }

        if (target == typeof(CuteArray))
        {
            return value.IsArray ? value.AsArray : null;
        }

        if (target == typeof(string))
        {
            return value.Type == CuteType.String ? value.AsString : value.ToDisplayString();
        }

        if (target.IsEnum)
        {
            // Written as a name, but a number is accepted so that data from another producer still
            // maps.
            return value.Type == CuteType.String
                ? Enum.Parse(target, value.AsString, ignoreCase: true)
                : Enum.ToObject(target, value.AsInt64);
        }

        if (target == typeof(bool))
        {
            return value.IsTruthy;
        }

        if (target == typeof(byte[]))
        {
            return value.Type == CuteType.Binary ? value.AsBinary : null;
        }

        if (target == typeof(DateTime))
        {
            return value.Type == CuteType.DateTime
                ? value.AsDateTime
                : DateTime.Parse(value.ToDisplayString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }

        if (target == typeof(DateTimeOffset))
        {
            return new DateTimeOffset((DateTime)FromValue(value, typeof(DateTime), naming)!, TimeSpan.Zero);
        }

        if (target == typeof(DateOnly))
        {
            return DateOnly.FromDateTime((DateTime)FromValue(value, typeof(DateTime), naming)!);
        }

        if (target == typeof(TimeSpan))
        {
            return value.IsNumber ? new TimeSpan(value.AsInt64) : TimeSpan.Parse(value.ToDisplayString(), CultureInfo.InvariantCulture);
        }

        if (target == typeof(Guid))
        {
            return value.Type == CuteType.Guid ? value.AsGuid : Guid.Parse(value.ToDisplayString());
        }

        if (target == typeof(CuteId))
        {
            return value.Type == CuteType.Id ? value.AsId : CuteId.Parse(value.ToDisplayString());
        }

        if (IsNumeric(target))
        {
            return ConvertNumber(value, target);
        }

        if (value.IsArray && TryBuildCollection(value.AsArray, target, naming, out var collection))
        {
            return collection;
        }

        if (value.IsObject)
        {
            if (typeof(IDictionary).IsAssignableFrom(target) && target.IsGenericType)
            {
                var valueType = target.GetGenericArguments()[^1];
                var dictionary = (IDictionary)Activator.CreateInstance(target)!;
                foreach (var (key, field) in value.AsObject)
                {
                    dictionary[key] = FromValue(field, valueType, naming);
                }

                return dictionary;
            }

            return ToPoco(value.AsObject, target, naming);
        }

        throw new CuteDbException($"Cannot map a {value.Type.ToDisplayName()} to {target.Name}.");
    }

    private static object ToPoco(CuteObject source, Type target, CuteNamingPolicy naming)
    {
        var map = CuteTypeMap.For(target, naming);
        if (map.Constructor is null)
        {
            throw new CuteDbException(
                $"{target.Name} has no parameterless constructor, so it cannot be materialised from a document. " +
                "Add one, or map through CuteDocument.");
        }

        var instance = map.Constructor.Invoke(null);

        foreach (var member in map.Members)
        {
            if (member.Set is null)
            {
                continue;
            }

            if (!source.TryGetValue(member.FieldName, out var field))
            {
                // Absent is not an error: documents in one collection need not share a shape, and
                // the property keeps its default.
                continue;
            }

            member.Set(instance, FromValue(field, member.Type, naming));
        }

        return instance;
    }

    private static bool TryBuildCollection(CuteArray source, Type target, CuteNamingPolicy naming, out object? result)
    {
        result = null;

        if (target.IsArray)
        {
            var element = target.GetElementType()!;
            var array = Array.CreateInstance(element, source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                array.SetValue(FromValue(source[i], element, naming), i);
            }

            result = array;
            return true;
        }

        var elementType = ElementTypeOf(target);
        if (elementType is null)
        {
            return false;
        }

        // A concrete List<T> for an interface target, so IEnumerable<T>, ICollection<T>,
        // IReadOnlyList<T> and friends all work without the caller having to think about it.
        var listType = typeof(List<>).MakeGenericType(elementType);
        var concrete = target.IsInterface || target.IsAbstract ? listType : target;

        if (Activator.CreateInstance(concrete) is not IList list)
        {
            return false;
        }

        foreach (var item in source.AsSpan().ToArray())
        {
            list.Add(FromValue(item, elementType, naming));
        }

        result = list;
        return true;
    }

    /// <summary>The loosely typed CLR shape of a value, for mapping into <c>object</c>.</summary>
    private static object? ToClrObject(CuteValue value) => value.Type switch
    {
        CuteType.Null or CuteType.Missing => null,
        CuteType.True => true,
        CuteType.False => false,
        CuteType.Int32 => value.AsInt32,
        CuteType.Int64 => value.AsInt64,
        CuteType.Double => value.AsDouble,
        CuteType.Decimal => value.AsDecimal,
        CuteType.String => value.AsString,
        CuteType.Binary => value.AsBinary,
        CuteType.DateTime => value.AsDateTime,
        CuteType.Guid => value.AsGuid,
        CuteType.Id => value.AsId,
        CuteType.Array => value.AsArray.AsSpan().ToArray().Select(ToClrObject).ToList(),
        CuteType.Object => value.AsObject.ToDictionary(f => f.Key, f => ToClrObject(f.Value)),
        _ => null,
    };

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
        or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
        or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static object ConvertNumber(CuteValue value, Type target) => Type.GetTypeCode(target) switch
    {
        TypeCode.SByte => (sbyte)value.AsInt64,
        TypeCode.Byte => (byte)value.AsInt64,
        TypeCode.Int16 => (short)value.AsInt64,
        TypeCode.UInt16 => (ushort)value.AsInt64,
        TypeCode.Int32 => value.AsInt32,
        TypeCode.UInt32 => (uint)value.AsInt64,
        TypeCode.Int64 => value.AsInt64,
        TypeCode.UInt64 => (ulong)value.AsInt64,
        TypeCode.Single => (float)value.AsDouble,
        TypeCode.Double => value.AsDouble,

        // Through the decimal path rather than through double, so a stored money value that was
        // exact stays exact.
        TypeCode.Decimal => value.Type == CuteType.Decimal ? value.AsDecimal : (decimal)value.AsDouble,
        _ => throw new CuteDbException($"{target.Name} is not a numeric type this mapper knows."),
    };

    private static Type? ElementTypeOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) || definition == typeof(List<>)
                || definition == typeof(IList<>) || definition == typeof(ICollection<>)
                || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(HashSet<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
