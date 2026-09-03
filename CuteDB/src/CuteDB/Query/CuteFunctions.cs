using System.Globalization;

namespace CuteDB.Query;

/// <summary>
/// The scalar and aggregate functions CuteQL understands.
/// </summary>
/// <remarks>
/// The set is deliberately small and made of things a document store actually needs: text
/// manipulation, arithmetic, null handling, type inspection, date parts, and array probing. Every
/// function returns <see cref="CuteValue.Missing"/> rather than throwing when handed the wrong
/// type, so one odd document in a million-row scan does not abort the query — the row simply fails
/// the predicate.
/// </remarks>
public static class CuteFunctions
{
    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX",
    };

    private static readonly HashSet<string> Scalars = new(StringComparer.OrdinalIgnoreCase)
    {
        "LENGTH", "UPPER", "LOWER", "TRIM", "SUBSTR", "CONCAT", "REPLACE", "SPLIT",
        "ABS", "ROUND", "FLOOR", "CEIL", "SQRT", "POW",
        "COALESCE", "IFNULL", "TYPEOF", "TOSTRING", "TONUMBER", "TOINT",
        "CONTAINS", "STARTSWITH", "ENDSWITH",
        "NOW", "YEAR", "MONTH", "DAY", "HOUR", "DATE_PART", "DATE_TRUNC",
        "ARRAY_LENGTH", "ELEMENT", "EXISTS", "KEYS",
    };

    /// <summary>Every function name, for help text and error messages.</summary>
    public static string NamesForHelp
        => string.Join(", ", Aggregates.Concat(Scalars).Select(n => n.ToUpperInvariant()).Order(StringComparer.Ordinal));

    /// <summary>True when the name is one of COUNT, SUM, AVG, MIN, MAX.</summary>
    public static bool IsAggregate(string name) => Aggregates.Contains(name);

    /// <summary>True when the name is a function CuteQL knows.</summary>
    public static bool IsKnown(string name) => Aggregates.Contains(name) || Scalars.Contains(name);

    /// <summary>Applies a scalar function to already-evaluated arguments.</summary>
    public static CuteValue Invoke(string name, ReadOnlySpan<CuteValue> args) => name switch
    {
        "LENGTH" => Length(args),
        "UPPER" => Text(args, static s => s.ToUpperInvariant()),
        "LOWER" => Text(args, static s => s.ToLowerInvariant()),
        "TRIM" => Text(args, static s => s.Trim()),
        "SUBSTR" => Substring(args),
        "CONCAT" => Concat(args),
        "REPLACE" => Replace(args),
        "SPLIT" => Split(args),

        "ABS" => Numeric(args, Math.Abs),
        "ROUND" => Round(args),
        "FLOOR" => Numeric(args, Math.Floor),
        "CEIL" => Numeric(args, Math.Ceiling),
        "SQRT" => Numeric(args, Math.Sqrt),
        "POW" => Power(args),

        "COALESCE" => Coalesce(args),
        "IFNULL" => args.Length == 2 ? (args[0].IsNullOrMissing ? args[1] : args[0]) : CuteValue.Missing,
        "TYPEOF" => args.Length == 1 ? CuteValue.String(args[0].Type.ToDisplayName()) : CuteValue.Missing,
        "TOSTRING" => args.Length == 1 ? CuteValue.String(args[0].ToDisplayString()) : CuteValue.Missing,
        "TONUMBER" => ToNumber(args),
        "TOINT" => ToInteger(args),

        "CONTAINS" => Contains(args),
        "STARTSWITH" => Affix(args, static (h, n) => h.StartsWith(n, StringComparison.Ordinal)),
        "ENDSWITH" => Affix(args, static (h, n) => h.EndsWith(n, StringComparison.Ordinal)),

        "NOW" => CuteValue.DateTime(DateTime.UtcNow),
        "YEAR" => DatePart(args, static d => d.Year),
        "MONTH" => DatePart(args, static d => d.Month),
        "DAY" => DatePart(args, static d => d.Day),
        "HOUR" => DatePart(args, static d => d.Hour),
        "DATE_PART" => NamedDatePart(args),
        "DATE_TRUNC" => DateTruncate(args),

        "ARRAY_LENGTH" => args.Length == 1 && args[0].IsArray ? CuteValue.Int32(args[0].Count) : CuteValue.Missing,
        "ELEMENT" => args.Length == 2 && args[1].IsNumber ? args[0][(int)args[1].AsInt64] : CuteValue.Missing,
        "EXISTS" => args.Length == 1 ? CuteValue.Boolean(!args[0].IsMissing) : CuteValue.Missing,
        "KEYS" => Keys(args),

        _ => throw new CuteDbException($"Function {name} has no implementation."),
    };

    /// <summary>
    /// Matches a value against a SQL <c>LIKE</c> pattern, where <c>%</c> stands for any run of
    /// characters and <c>_</c> for exactly one. A backslash escapes either.
    /// </summary>
    public static bool Like(CuteValue value, CuteValue pattern)
    {
        if (!value.TryGetString(out var text) || !pattern.TryGetString(out var patternText))
        {
            return false;
        }

        return LikeMatch(text.AsSpan(), patternText.AsSpan());
    }

    private static bool LikeMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        // An iterative matcher with a single backtrack point. A '%' can match any length, so when
        // a later mismatch happens the scan resumes one character past where that '%' started
        // matching — which is enough for the one-wildcard-class grammar LIKE has, and avoids the
        // exponential blowup a naive recursive matcher shows on patterns like '%a%a%a%a%b'.
        var textIndex = 0;
        var patternIndex = 0;
        var starText = -1;
        var starPattern = -1;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '\\' && patternIndex + 1 < pattern.Length)
            {
                if (text[textIndex] == pattern[patternIndex + 1])
                {
                    textIndex++;
                    patternIndex += 2;
                    continue;
                }
            }
            else if (patternIndex < pattern.Length && (pattern[patternIndex] == '_' || pattern[patternIndex] == text[textIndex]))
            {
                textIndex++;
                patternIndex++;
                continue;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '%')
            {
                starPattern = patternIndex++;
                starText = textIndex;
                continue;
            }

            if (starPattern < 0)
            {
                return false;
            }

            patternIndex = starPattern + 1;
            textIndex = ++starText;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '%')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static CuteValue Length(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 1)
        {
            return CuteValue.Missing;
        }

        return args[0].Type switch
        {
            CuteType.String or CuteType.Array or CuteType.Object or CuteType.Binary => CuteValue.Int32(args[0].Count),
            _ => CuteValue.Missing,
        };
    }

    private static CuteValue Text(ReadOnlySpan<CuteValue> args, Func<string, string> transform)
        => args.Length == 1 && args[0].TryGetString(out var text)
            ? CuteValue.String(transform(text))
            : CuteValue.Missing;

    private static CuteValue Substring(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length is < 2 or > 3 || !args[0].TryGetString(out var text) || !args[1].IsNumber)
        {
            return CuteValue.Missing;
        }

        var start = (int)args[1].AsInt64;
        if (start < 0)
        {
            start = Math.Max(0, text.Length + start);
        }

        if (start >= text.Length)
        {
            return CuteValue.String(string.Empty);
        }

        var length = args.Length == 3 && args[2].IsNumber ? (int)args[2].AsInt64 : text.Length - start;
        length = Math.Clamp(length, 0, text.Length - start);
        return CuteValue.String(text.Substring(start, length));
    }

    private static CuteValue Concat(ReadOnlySpan<CuteValue> args)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var arg in args)
        {
            if (!arg.IsNullOrMissing)
            {
                builder.Append(arg.ToDisplayString());
            }
        }

        return CuteValue.String(builder.ToString());
    }

    private static CuteValue Replace(ReadOnlySpan<CuteValue> args)
        => args.Length == 3 && args[0].TryGetString(out var text)
            && args[1].TryGetString(out var find) && args[2].TryGetString(out var with) && find.Length > 0
            ? CuteValue.String(text.Replace(find, with, StringComparison.Ordinal))
            : CuteValue.Missing;

    private static CuteValue Split(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 2 || !args[0].TryGetString(out var text) || !args[1].TryGetString(out var separator))
        {
            return CuteValue.Missing;
        }

        var parts = separator.Length == 0 ? [text] : text.Split(separator, StringSplitOptions.None);
        var array = new CuteArray(parts.Length);
        foreach (var part in parts)
        {
            array.Add(CuteValue.String(part));
        }

        return CuteValue.Array(array);
    }

    private static CuteValue Numeric(ReadOnlySpan<CuteValue> args, Func<double, double> transform)
    {
        if (args.Length != 1 || !args[0].IsNumber)
        {
            return CuteValue.Missing;
        }

        // Keeping decimals as decimals through ABS/FLOOR/CEIL matters for money; only the
        // genuinely floating operations widen.
        if (args[0].Type == CuteType.Decimal)
        {
            var asDecimal = args[0].AsDecimal;
            var transformed = transform((double)asDecimal);
            if (transformed == Math.Truncate(transformed) && Math.Abs(transformed) < 7.9e28)
            {
                return CuteValue.Decimal((decimal)transformed);
            }
        }

        return CuteValue.Double(transform(args[0].AsDouble));
    }

    private static CuteValue Round(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length is < 1 or > 2 || !args[0].IsNumber)
        {
            return CuteValue.Missing;
        }

        var digits = args.Length == 2 && args[1].IsNumber ? (int)args[1].AsInt64 : 0;
        digits = Math.Clamp(digits, 0, 15);

        return args[0].Type == CuteType.Decimal
            ? CuteValue.Decimal(Math.Round(args[0].AsDecimal, digits, MidpointRounding.AwayFromZero))
            : CuteValue.Double(Math.Round(args[0].AsDouble, digits, MidpointRounding.AwayFromZero));
    }

    private static CuteValue Power(ReadOnlySpan<CuteValue> args)
        => args.Length == 2 && args[0].IsNumber && args[1].IsNumber
            ? CuteValue.Double(Math.Pow(args[0].AsDouble, args[1].AsDouble))
            : CuteValue.Missing;

    private static CuteValue Coalesce(ReadOnlySpan<CuteValue> args)
    {
        foreach (var arg in args)
        {
            if (!arg.IsNullOrMissing)
            {
                return arg;
            }
        }

        return CuteValue.Null;
    }

    private static CuteValue ToNumber(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 1)
        {
            return CuteValue.Missing;
        }

        if (args[0].IsNumber)
        {
            return args[0];
        }

        return args[0].TryGetString(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? CuteValue.Double(parsed)
            : CuteValue.Missing;
    }

    private static CuteValue ToInteger(ReadOnlySpan<CuteValue> args)
    {
        var number = ToNumber(args);
        return number.IsNumber ? CuteValue.Int64(number.AsInt64) : CuteValue.Missing;
    }

    private static CuteValue Contains(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 2)
        {
            return CuteValue.Missing;
        }

        if (args[0].IsArray)
        {
            foreach (var item in args[0].AsArray.AsSpan())
            {
                if (CuteValueComparer.Equal(item, args[1]))
                {
                    return CuteValue.Boolean(true);
                }
            }

            return CuteValue.Boolean(false);
        }

        return args[0].TryGetString(out var haystack) && args[1].TryGetString(out var needle)
            ? CuteValue.Boolean(haystack.Contains(needle, StringComparison.Ordinal))
            : CuteValue.Missing;
    }

    private static CuteValue Affix(ReadOnlySpan<CuteValue> args, Func<string, string, bool> test)
        => args.Length == 2 && args[0].TryGetString(out var haystack) && args[1].TryGetString(out var needle)
            ? CuteValue.Boolean(test(haystack, needle))
            : CuteValue.Missing;

    private static CuteValue DatePart(ReadOnlySpan<CuteValue> args, Func<DateTime, int> part)
        => args.Length == 1 && args[0].Type == CuteType.DateTime
            ? CuteValue.Int32(part(args[0].AsDateTime))
            : CuteValue.Missing;

    private static CuteValue NamedDatePart(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 2 || !args[0].TryGetString(out var part) || args[1].Type != CuteType.DateTime)
        {
            return CuteValue.Missing;
        }

        var date = args[1].AsDateTime;
        return part.ToUpperInvariant() switch
        {
            "YEAR" => CuteValue.Int32(date.Year),
            "QUARTER" => CuteValue.Int32(((date.Month - 1) / 3) + 1),
            "MONTH" => CuteValue.Int32(date.Month),
            "WEEK" => CuteValue.Int32(ISOWeek.GetWeekOfYear(date)),
            "DAY" => CuteValue.Int32(date.Day),
            "DAYOFWEEK" => CuteValue.Int32((int)date.DayOfWeek),
            "DAYOFYEAR" => CuteValue.Int32(date.DayOfYear),
            "HOUR" => CuteValue.Int32(date.Hour),
            "MINUTE" => CuteValue.Int32(date.Minute),
            "SECOND" => CuteValue.Int32(date.Second),
            _ => CuteValue.Missing,
        };
    }

    private static CuteValue DateTruncate(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 2 || !args[0].TryGetString(out var unit) || args[1].Type != CuteType.DateTime)
        {
            return CuteValue.Missing;
        }

        var date = args[1].AsDateTime;
        return unit.ToUpperInvariant() switch
        {
            "YEAR" => CuteValue.DateTime(new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            "QUARTER" => CuteValue.DateTime(new DateTime(date.Year, (((date.Month - 1) / 3) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            "MONTH" => CuteValue.DateTime(new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc)),
            "WEEK" => CuteValue.DateTime(date.Date.AddDays(-(int)date.DayOfWeek)),
            "DAY" => CuteValue.DateTime(date.Date),
            "HOUR" => CuteValue.DateTime(new DateTime(date.Year, date.Month, date.Day, date.Hour, 0, 0, DateTimeKind.Utc)),
            _ => CuteValue.Missing,
        };
    }

    private static CuteValue Keys(ReadOnlySpan<CuteValue> args)
    {
        if (args.Length != 1 || !args[0].IsObject)
        {
            return CuteValue.Missing;
        }

        var source = args[0].AsObject;
        var array = new CuteArray(source.Count);
        foreach (var key in source.Keys)
        {
            array.Add(CuteValue.String(key));
        }

        return CuteValue.Array(array);
    }
}
