using System.Runtime.CompilerServices;

namespace CuteDB;

/// <summary>
/// Equality, ordering and hashing for <see cref="CuteValue"/>.
/// </summary>
/// <remarks>
/// <para>
/// These rules are the semantics of every CuteQL comparison, every index lookup and every
/// <c>ORDER BY</c>, and the Rust accelerator reimplements them byte for byte. Changing anything
/// here means changing <c>native/cutedb-core/src/compare.rs</c> in the same commit, or the
/// managed and native scanners will disagree — which the parity tests in
/// <c>CuteDB.Tests/NativeParityTests.cs</c> exist to catch.
/// </para>
/// <para>
/// Two decisions are worth stating outright. Numbers compare across their four representations,
/// so <c>1</c>, <c>1L</c>, <c>1.0</c> and <c>1.0m</c> are one value; a schemaless store where the
/// same field arrives sometimes as an integer and sometimes as a double is the normal case, not
/// an error. And values of different types still have a defined order, ranked
/// missing &lt; null &lt; bool &lt; number &lt; string &lt; binary &lt; datetime &lt; guid &lt;
/// id &lt; array &lt; object, so sorting a field with mixed contents is deterministic instead of
/// throwing.
/// </para>
/// </remarks>
public static class CuteValueComparer
{
    /// <summary>
    /// The sort rank of a type. Values of different types order by rank; values of the same rank
    /// order by their payload.
    /// </summary>
    public static int TypeRank(CuteType type) => type switch
    {
        CuteType.Missing => 0,
        CuteType.Null => 1,
        CuteType.False or CuteType.True => 2,
        CuteType.Int32 or CuteType.Int64 or CuteType.Double or CuteType.Decimal => 3,
        CuteType.String => 4,
        CuteType.Binary => 5,
        CuteType.DateTime => 6,
        CuteType.Guid => 7,
        CuteType.Id => 8,
        CuteType.Array => 9,
        CuteType.Object => 10,
        _ => 11,
    };

    /// <summary>Value equality with cross-numeric coercion.</summary>
    public static bool Equal(CuteValue left, CuteValue right)
    {
        if (left.Type != right.Type)
        {
            // The one cross-type case that is still equality: two different numeric encodings of
            // the same number. Everything else is unequal by construction.
            return left.IsNumber && right.IsNumber && CompareNumbers(left, right) == 0;
        }

        return left.Type switch
        {
            CuteType.Null or CuteType.Missing or CuteType.True or CuteType.False => true,
            CuteType.Int32 or CuteType.Int64 or CuteType.Double or CuteType.Decimal => CompareNumbers(left, right) == 0,
            CuteType.String => string.Equals(left.AsString, right.AsString, StringComparison.Ordinal),
            CuteType.Binary => left.AsBinary.AsSpan().SequenceEqual(right.AsBinary),
            CuteType.DateTime => left.AsDateTime == right.AsDateTime,
            CuteType.Guid => left.AsGuid == right.AsGuid,
            CuteType.Id => left.AsId == right.AsId,
            CuteType.Array => ArrayEqual(left.AsArray, right.AsArray),
            CuteType.Object => ObjectEqual(left.AsObject, right.AsObject),
            _ => false,
        };
    }

    /// <summary>Total ordering. Negative, zero or positive in the usual way.</summary>
    public static int Compare(CuteValue left, CuteValue right)
    {
        var leftRank = TypeRank(left.Type);
        var rightRank = TypeRank(right.Type);
        if (leftRank != rightRank)
        {
            return leftRank < rightRank ? -1 : 1;
        }

        return leftRank switch
        {
            0 or 1 => 0,
            2 => (left.Type == CuteType.True ? 1 : 0).CompareTo(right.Type == CuteType.True ? 1 : 0),
            3 => CompareNumbers(left, right),
            4 => string.CompareOrdinal(left.AsString, right.AsString),
            5 => left.AsBinary.AsSpan().SequenceCompareTo(right.AsBinary),
            6 => left.AsDateTime.CompareTo(right.AsDateTime),
            7 => left.AsGuid.CompareTo(right.AsGuid),
            8 => left.AsId.CompareTo(right.AsId),
            9 => CompareArrays(left.AsArray, right.AsArray),
            10 => CompareObjects(left.AsObject, right.AsObject),
            _ => 0,
        };
    }

    /// <summary>A hash consistent with <see cref="Equal"/>.</summary>
    public static int GetHashCode(CuteValue value)
    {
        switch (value.Type)
        {
            case CuteType.Missing:
                return 0;
            case CuteType.Null:
                return 1;
            case CuteType.False:
                return 2;
            case CuteType.True:
                return 3;

            case CuteType.Int32:
            case CuteType.Int64:
            case CuteType.Double:
            case CuteType.Decimal:
                // Every numeric encoding of the same number has to hash alike, so an integral
                // value always hashes through the long path regardless of how it was stored.
                return TryGetExactInt64(value, out var integral)
                    ? HashCode.Combine(3, integral)
                    : HashCode.Combine(3, value.AsDouble);

            case CuteType.String:
                return HashCode.Combine(4, string.GetHashCode(value.AsString, StringComparison.Ordinal));

            case CuteType.Binary:
            {
                var hash = new HashCode();
                hash.Add(5);
                hash.AddBytes(value.AsBinary);
                return hash.ToHashCode();
            }

            case CuteType.DateTime:
                return HashCode.Combine(6, value.AsDateTime);
            case CuteType.Guid:
                return HashCode.Combine(7, value.AsGuid);
            case CuteType.Id:
                return HashCode.Combine(8, value.AsId);

            case CuteType.Array:
            {
                var hash = new HashCode();
                hash.Add(9);
                foreach (var item in value.AsArray.AsSpan())
                {
                    hash.Add(GetHashCode(item));
                }

                return hash.ToHashCode();
            }

            case CuteType.Object:
            {
                // Field order must not affect the hash, because Equal ignores it.
                var accumulated = 10;
                foreach (var (key, field) in value.AsObject)
                {
                    accumulated ^= HashCode.Combine(
                        string.GetHashCode(key, StringComparison.Ordinal),
                        GetHashCode(field));
                }

                return accumulated;
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// Compares two numeric values, widening only as far as it has to: integers stay integers,
    /// decimals stay decimals, and the lossy double path is taken only when one side really is a
    /// double.
    /// </summary>
    public static int CompareNumbers(CuteValue left, CuteValue right)
    {
        var leftIntegral = left.Type is CuteType.Int32 or CuteType.Int64;
        var rightIntegral = right.Type is CuteType.Int32 or CuteType.Int64;
        if (leftIntegral && rightIntegral)
        {
            return left.AsInt64.CompareTo(right.AsInt64);
        }

        if (left.Type != CuteType.Double && right.Type != CuteType.Double)
        {
            // Only integers and decimals are in play, so decimal compares them exactly.
            return ToDecimal(left).CompareTo(ToDecimal(right));
        }

        var leftDouble = left.AsDouble;
        var rightDouble = right.AsDouble;

        // NaN is ordered below every other number and is equal to itself, so that grouping and
        // sorting on a field containing NaN stay well defined.
        var leftNaN = double.IsNaN(leftDouble);
        var rightNaN = double.IsNaN(rightDouble);
        if (leftNaN || rightNaN)
        {
            return leftNaN && rightNaN ? 0 : leftNaN ? -1 : 1;
        }

        return leftDouble.CompareTo(rightDouble);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal ToDecimal(CuteValue value) => value.Type switch
    {
        CuteType.Int32 or CuteType.Int64 => value.AsInt64,
        CuteType.Decimal => value.AsDecimal,
        _ => (decimal)value.AsDouble,
    };

    private static bool TryGetExactInt64(CuteValue value, out long result)
    {
        switch (value.Type)
        {
            case CuteType.Int32:
            case CuteType.Int64:
                result = value.AsInt64;
                return true;

            case CuteType.Double:
            {
                var d = value.AsDouble;
                if (double.IsInteger(d) && d is >= -9.007199254740992E15 and <= 9.007199254740992E15)
                {
                    result = (long)d;
                    return true;
                }

                break;
            }

            case CuteType.Decimal:
            {
                var m = value.AsDecimal;
                if (decimal.Truncate(m) == m && m is >= long.MinValue and <= long.MaxValue)
                {
                    result = (long)m;
                    return true;
                }

                break;
            }
        }

        result = 0;
        return false;
    }

    private static bool ArrayEqual(CuteArray left, CuteArray right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        var leftItems = left.AsSpan();
        var rightItems = right.AsSpan();
        for (var i = 0; i < leftItems.Length; i++)
        {
            if (!Equal(leftItems[i], rightItems[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ObjectEqual(CuteObject left, CuteObject right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        // Field order is not part of an object's identity, so each field is looked up by name.
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !Equal(value, other))
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareArrays(CuteArray left, CuteArray right)
    {
        var leftItems = left.AsSpan();
        var rightItems = right.AsSpan();
        var shared = Math.Min(leftItems.Length, rightItems.Length);
        for (var i = 0; i < shared; i++)
        {
            var order = Compare(leftItems[i], rightItems[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return leftItems.Length.CompareTo(rightItems.Length);
    }

    private static int CompareObjects(CuteObject left, CuteObject right)
    {
        if (left.Count != right.Count)
        {
            return left.Count.CompareTo(right.Count);
        }

        // Equal ignores field order, so Compare has to as well or the two would contradict each
        // other. Walking both in sorted key order gives a stable answer either way.
        var leftKeys = left.Keys.ToArray();
        var rightKeys = right.Keys.ToArray();
        Array.Sort(leftKeys, StringComparer.Ordinal);
        Array.Sort(rightKeys, StringComparer.Ordinal);

        for (var i = 0; i < leftKeys.Length; i++)
        {
            var byKey = string.CompareOrdinal(leftKeys[i], rightKeys[i]);
            if (byKey != 0)
            {
                return byKey;
            }
        }

        foreach (var key in leftKeys)
        {
            var order = Compare(left[key], right[key]);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}

/// <summary>
/// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> over
/// <see cref="CuteValue"/>, for the places that need one as an object (dictionary keys, sorts).
/// </summary>
public sealed class CuteValueEqualityComparer : IEqualityComparer<CuteValue>, IComparer<CuteValue>
{
    /// <summary>The shared instance.</summary>
    public static CuteValueEqualityComparer Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals(CuteValue x, CuteValue y) => CuteValueComparer.Equal(x, y);

    /// <inheritdoc />
    public int GetHashCode(CuteValue obj) => CuteValueComparer.GetHashCode(obj);

    /// <inheritdoc />
    public int Compare(CuteValue x, CuteValue y) => CuteValueComparer.Compare(x, y);
}
