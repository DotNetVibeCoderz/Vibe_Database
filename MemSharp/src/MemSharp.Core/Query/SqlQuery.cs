using System.Globalization;
using MemSharp.Collections;

namespace MemSharp.Query;

/// <summary>Which column a predicate or sort refers to.</summary>
public enum QueryColumn
{
    /// <summary>The key name.</summary>
    Key,
    /// <summary>The <see cref="MemType"/>, compared by name.</summary>
    Type,
    /// <summary>String length, or element count for a collection.</summary>
    Size,
    /// <summary>Remaining lifetime in seconds; <c>null</c> for a permanent key.</summary>
    Ttl,
    /// <summary>The value, for string keys only.</summary>
    Value,
}

/// <summary>What a parsed <c>SELECT</c> or <c>DELETE</c> asked for.</summary>
public sealed class SqlQuery
{
    /// <summary>True for <c>DELETE</c>, false for <c>SELECT</c>.</summary>
    public bool IsDelete { get; init; }

    /// <summary>Columns to project, in order. Empty means every column.</summary>
    public IReadOnlyList<QueryColumn> Columns { get; init; } = Array.Empty<QueryColumn>();

    /// <summary>The filter, or <c>null</c> to match every key.</summary>
    public QueryPredicate? Where { get; init; }

    /// <summary>Sort column, or <c>null</c> to leave the order unspecified.</summary>
    public QueryColumn? OrderBy { get; init; }

    /// <summary>True if <c>ORDER BY ... DESC</c> was given.</summary>
    public bool Descending { get; init; }

    /// <summary>Row limit, or -1 for unlimited.</summary>
    public int Limit { get; init; } = -1;

    /// <summary>Rows to skip before the limit applies.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// A key pattern the planner can push down to <see cref="MemDb.Scan"/>, avoiding a full walk.
    /// <c>null</c> when the filter cannot be narrowed to one prefix.
    /// </summary>
    public string? KeyPattern { get; init; }
}

/// <summary>A node in a <c>WHERE</c> clause.</summary>
public abstract class QueryPredicate
{
    /// <summary>Evaluates the predicate against one key.</summary>
    public abstract bool Matches(in KeyInfo key, DateTimeOffset now);
}

internal sealed class AndPredicate(QueryPredicate left, QueryPredicate right) : QueryPredicate
{
    public QueryPredicate Left { get; } = left;
    public QueryPredicate Right { get; } = right;
    public override bool Matches(in KeyInfo key, DateTimeOffset now) => Left.Matches(key, now) && Right.Matches(key, now);
}

internal sealed class OrPredicate(QueryPredicate left, QueryPredicate right) : QueryPredicate
{
    public QueryPredicate Left { get; } = left;
    public QueryPredicate Right { get; } = right;
    public override bool Matches(in KeyInfo key, DateTimeOffset now) => Left.Matches(key, now) || Right.Matches(key, now);
}

internal sealed class NotPredicate(QueryPredicate inner) : QueryPredicate
{
    public QueryPredicate Inner { get; } = inner;
    public override bool Matches(in KeyInfo key, DateTimeOffset now) => !Inner.Matches(key, now);
}

/// <summary>The comparisons a <c>WHERE</c> clause can make.</summary>
public enum ComparisonOperator
{
    /// <summary>Equality.</summary>
    Equal,
    /// <summary>Inequality.</summary>
    NotEqual,
    /// <summary>Strictly less than.</summary>
    LessThan,
    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,
    /// <summary>Strictly greater than.</summary>
    GreaterThan,
    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual,
    /// <summary>SQL <c>LIKE</c>, with <c>%</c> and <c>_</c> wildcards.</summary>
    Like,
    /// <summary>Membership in a literal list.</summary>
    In,
}

internal sealed class ComparisonPredicate : QueryPredicate
{
    private readonly string? _globPattern;

    public ComparisonPredicate(QueryColumn column, ComparisonOperator op, string[] operands)
    {
        Column = column;
        Operator = op;
        Operands = operands;

        // LIKE patterns are translated to glob syntax once, at parse time. Doing it per row would
        // rebuild the same string for every key in the database.
        if (op == ComparisonOperator.Like) _globPattern = GlobMatcher.FromSqlLike(operands[0]);
    }

    public QueryColumn Column { get; }
    public ComparisonOperator Operator { get; }
    public string[] Operands { get; }

    public override bool Matches(in KeyInfo key, DateTimeOffset now)
    {
        if (Operator == ComparisonOperator.In)
        {
            string? text = TextOf(key, now);
            if (text is null) return false;
            foreach (var candidate in Operands)
            {
                if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        if (Operator == ComparisonOperator.Like)
        {
            string? text = TextOf(key, now);
            return text is not null && GlobMatcher.IsMatch(_globPattern!, text);
        }

        // Numeric columns compare numerically; everything else compares as text. Without this a
        // predicate like `size > 9` would put 10 before 9, which is exactly the kind of quiet wrong
        // answer that makes a query layer untrustworthy.
        if (Column is QueryColumn.Size or QueryColumn.Ttl)
        {
            double? left = NumberOf(key, now);
            if (left is null) return Operator == ComparisonOperator.NotEqual;
            if (!double.TryParse(Operands[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double right)) return false;
            return Compare(left.Value.CompareTo(right));
        }

        string? actual = TextOf(key, now);
        if (actual is null) return Operator == ComparisonOperator.NotEqual;
        return Compare(string.Compare(actual, Operands[0], StringComparison.OrdinalIgnoreCase));
    }

    private bool Compare(int ordering) => Operator switch
    {
        ComparisonOperator.Equal => ordering == 0,
        ComparisonOperator.NotEqual => ordering != 0,
        ComparisonOperator.LessThan => ordering < 0,
        ComparisonOperator.LessThanOrEqual => ordering <= 0,
        ComparisonOperator.GreaterThan => ordering > 0,
        ComparisonOperator.GreaterThanOrEqual => ordering >= 0,
        _ => false,
    };

    private string? TextOf(in KeyInfo key, DateTimeOffset now) => Column switch
    {
        QueryColumn.Key => key.Key,
        QueryColumn.Type => key.Type.ToString(),
        QueryColumn.Size => key.Size.ToString(CultureInfo.InvariantCulture),
        QueryColumn.Ttl => TtlSeconds(key, now)?.ToString("0.###", CultureInfo.InvariantCulture),
        QueryColumn.Value => key.StringValue,
        _ => null,
    };

    private double? NumberOf(in KeyInfo key, DateTimeOffset now) => Column switch
    {
        QueryColumn.Size => key.Size,
        QueryColumn.Ttl => TtlSeconds(key, now),
        _ => null,
    };

    internal static double? TtlSeconds(in KeyInfo key, DateTimeOffset now) =>
        key.ExpiresAt is { } expiry ? (expiry - now).TotalSeconds : null;
}
