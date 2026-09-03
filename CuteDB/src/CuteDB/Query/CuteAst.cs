namespace CuteDB.Query;

/// <summary>Binary operators in a CuteQL expression.</summary>
public enum CuteBinaryOperator
{
    And,
    Or,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Like,
    NotLike,
}

/// <summary>Unary operators in a CuteQL expression.</summary>
public enum CuteUnaryOperator
{
    Not,
    Negate,
}

/// <summary>Base class for every expression node.</summary>
public abstract class CuteExpression
{
    /// <summary>Offset in the query text this node started at, for error reporting.</summary>
    public int Position { get; init; }

    /// <summary>Renders the expression back to CuteQL text.</summary>
    public abstract override string ToString();
}

/// <summary>A constant.</summary>
public sealed class LiteralExpression(CuteValue value) : CuteExpression
{
    /// <summary>The constant value.</summary>
    public CuteValue Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value.Type == CuteType.String ? $"'{Value.AsString}'" : Value.ToDisplayString();
}

/// <summary>A named parameter, supplied separately from the query text.</summary>
public sealed class ParameterExpression(string name) : CuteExpression
{
    /// <summary>The parameter name, without its <c>@</c> or <c>$</c> prefix.</summary>
    public string Name { get; } = name;

    /// <inheritdoc />
    public override string ToString() => "@" + Name;
}

/// <summary>A document field path.</summary>
public sealed class PathExpression(CutePath path) : CuteExpression
{
    /// <summary>The compiled path.</summary>
    public CutePath Path { get; } = path;

    /// <inheritdoc />
    public override string ToString() => Path.Text;
}

/// <summary>The <c>*</c> in <c>SELECT *</c> or <c>COUNT(*)</c>.</summary>
public sealed class StarExpression : CuteExpression
{
    /// <inheritdoc />
    public override string ToString() => "*";
}

/// <summary>An operator with one operand.</summary>
public sealed class UnaryExpression(CuteUnaryOperator op, CuteExpression operand) : CuteExpression
{
    /// <summary>The operator.</summary>
    public CuteUnaryOperator Operator { get; } = op;

    /// <summary>The operand.</summary>
    public CuteExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override string ToString() => Operator == CuteUnaryOperator.Not ? $"NOT {Operand}" : $"-{Operand}";
}

/// <summary>An operator with two operands.</summary>
public sealed class BinaryExpression(CuteBinaryOperator op, CuteExpression left, CuteExpression right) : CuteExpression
{
    /// <summary>The operator.</summary>
    public CuteBinaryOperator Operator { get; } = op;

    /// <summary>The left operand.</summary>
    public CuteExpression Left { get; } = left;

    /// <summary>The right operand.</summary>
    public CuteExpression Right { get; } = right;

    /// <summary>The operator's CuteQL spelling.</summary>
    public string Symbol => Operator switch
    {
        CuteBinaryOperator.And => "AND",
        CuteBinaryOperator.Or => "OR",
        CuteBinaryOperator.Equal => "=",
        CuteBinaryOperator.NotEqual => "!=",
        CuteBinaryOperator.Less => "<",
        CuteBinaryOperator.LessOrEqual => "<=",
        CuteBinaryOperator.Greater => ">",
        CuteBinaryOperator.GreaterOrEqual => ">=",
        CuteBinaryOperator.Add => "+",
        CuteBinaryOperator.Subtract => "-",
        CuteBinaryOperator.Multiply => "*",
        CuteBinaryOperator.Divide => "/",
        CuteBinaryOperator.Modulo => "%",
        CuteBinaryOperator.Like => "LIKE",
        CuteBinaryOperator.NotLike => "NOT LIKE",
        _ => "?",
    };

    /// <inheritdoc />
    public override string ToString() => $"({Left} {Symbol} {Right})";
}

/// <summary><c>value BETWEEN low AND high</c>.</summary>
public sealed class BetweenExpression(CuteExpression value, CuteExpression low, CuteExpression high, bool negated) : CuteExpression
{
    /// <summary>The value being tested.</summary>
    public CuteExpression Value { get; } = value;

    /// <summary>The inclusive lower bound.</summary>
    public CuteExpression Low { get; } = low;

    /// <summary>The inclusive upper bound.</summary>
    public CuteExpression High { get; } = high;

    /// <summary>True for <c>NOT BETWEEN</c>.</summary>
    public bool Negated { get; } = negated;

    /// <inheritdoc />
    public override string ToString() => $"({Value} {(Negated ? "NOT " : string.Empty)}BETWEEN {Low} AND {High})";
}

/// <summary><c>value IN (a, b, c)</c>.</summary>
public sealed class InExpression(CuteExpression value, IReadOnlyList<CuteExpression> items, bool negated) : CuteExpression
{
    /// <summary>The value being tested.</summary>
    public CuteExpression Value { get; } = value;

    /// <summary>The candidate set.</summary>
    public IReadOnlyList<CuteExpression> Items { get; } = items;

    /// <summary>True for <c>NOT IN</c>.</summary>
    public bool Negated { get; } = negated;

    /// <inheritdoc />
    public override string ToString()
        => $"({Value} {(Negated ? "NOT " : string.Empty)}IN ({string.Join(", ", Items)}))";
}

/// <summary><c>value IS NULL</c>, <c>IS NOT NULL</c> or <c>IS MISSING</c>.</summary>
public sealed class IsExpression(CuteExpression value, bool negated, bool missing) : CuteExpression
{
    /// <summary>The value being tested.</summary>
    public CuteExpression Value { get; } = value;

    /// <summary>True for the <c>NOT</c> form.</summary>
    public bool Negated { get; } = negated;

    /// <summary>
    /// True for <c>IS MISSING</c>, which asks whether the field is absent, as opposed to
    /// <c>IS NULL</c>, which asks whether it is absent <em>or</em> explicitly null.
    /// </summary>
    public bool Missing { get; } = missing;

    /// <inheritdoc />
    public override string ToString()
        => $"({Value} IS {(Negated ? "NOT " : string.Empty)}{(Missing ? "MISSING" : "NULL")})";
}

/// <summary>A function call.</summary>
public sealed class FunctionExpression(string name, IReadOnlyList<CuteExpression> arguments) : CuteExpression
{
    /// <summary>The function name, uppercased.</summary>
    public string Name { get; } = name.ToUpperInvariant();

    /// <summary>The arguments.</summary>
    public IReadOnlyList<CuteExpression> Arguments { get; } = arguments;

    /// <summary>True when this is one of COUNT, SUM, AVG, MIN, MAX.</summary>
    public bool IsAggregate => CuteFunctions.IsAggregate(Name);

    /// <inheritdoc />
    public override string ToString() => $"{Name}({string.Join(", ", Arguments)})";
}

/// <summary>An array literal.</summary>
public sealed class ArrayExpression(IReadOnlyList<CuteExpression> items) : CuteExpression
{
    /// <summary>The elements.</summary>
    public IReadOnlyList<CuteExpression> Items { get; } = items;

    /// <inheritdoc />
    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

/// <summary>An object literal.</summary>
public sealed class ObjectExpression(IReadOnlyList<KeyValuePair<string, CuteExpression>> fields) : CuteExpression
{
    /// <summary>The fields, in source order.</summary>
    public IReadOnlyList<KeyValuePair<string, CuteExpression>> Fields { get; } = fields;

    /// <inheritdoc />
    public override string ToString()
        => $"{{{string.Join(", ", Fields.Select(f => $"'{f.Key}': {f.Value}"))}}}";
}

/// <summary>One item in a <c>SELECT</c> list.</summary>
/// <param name="Expression">What to compute.</param>
/// <param name="Alias">The output field name, or null to derive one.</param>
public sealed record CuteProjection(CuteExpression Expression, string? Alias)
{
    /// <summary>The name this projection produces in the result.</summary>
    public string OutputName => Alias ?? Expression switch
    {
        PathExpression path => path.Path.Text,
        FunctionExpression function => function.ToString(),
        _ => Expression.ToString(),
    };
}

/// <summary>One <c>ORDER BY</c> term.</summary>
/// <param name="Expression">What to sort on.</param>
/// <param name="Descending">Whether to sort descending.</param>
public sealed record CuteOrdering(CuteExpression Expression, bool Descending);

/// <summary>One <c>SET</c> assignment in an <c>UPDATE</c>.</summary>
/// <param name="Path">Where to write.</param>
/// <param name="Value">What to write.</param>
public sealed record CuteAssignment(CutePath Path, CuteExpression Value);

/// <summary>Base class for every parsed statement.</summary>
public abstract class CuteStatement
{
    /// <summary>The collection the statement runs against.</summary>
    public required string Collection { get; init; }

    /// <summary>The original query text.</summary>
    public required string Text { get; init; }
}

/// <summary>A parsed <c>SELECT</c>.</summary>
public sealed class SelectStatement : CuteStatement
{
    /// <summary>The projection list. Empty means <c>SELECT *</c>.</summary>
    public required IReadOnlyList<CuteProjection> Projections { get; init; }

    /// <summary>True for <c>SELECT *</c>.</summary>
    public bool IsSelectAll => Projections.Count == 0;

    /// <summary>The filter, or null.</summary>
    public CuteExpression? Where { get; init; }

    /// <summary>The grouping keys, if any.</summary>
    public IReadOnlyList<CuteExpression> GroupBy { get; init; } = [];

    /// <summary>The post-grouping filter, or null.</summary>
    public CuteExpression? Having { get; init; }

    /// <summary>The sort terms, if any.</summary>
    public IReadOnlyList<CuteOrdering> OrderBy { get; init; } = [];

    /// <summary>The row cap, or null for no limit.</summary>
    public int? Limit { get; init; }

    /// <summary>How many rows to skip.</summary>
    public int Offset { get; init; }

    /// <summary>True for <c>SELECT DISTINCT</c>.</summary>
    public bool Distinct { get; init; }

    /// <summary>True when the projection list contains an aggregate.</summary>
    public bool HasAggregates => Projections.Any(p => ContainsAggregate(p.Expression));

    internal static bool ContainsAggregate(CuteExpression expression) => expression switch
    {
        FunctionExpression function => function.IsAggregate || function.Arguments.Any(ContainsAggregate),
        BinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
        UnaryExpression unary => ContainsAggregate(unary.Operand),
        _ => false,
    };
}

/// <summary>A parsed <c>DELETE</c>.</summary>
public sealed class DeleteStatement : CuteStatement
{
    /// <summary>The filter, or null to delete everything.</summary>
    public CuteExpression? Where { get; init; }
}

/// <summary>A parsed <c>UPDATE</c>.</summary>
public sealed class UpdateStatement : CuteStatement
{
    /// <summary>The assignments to apply.</summary>
    public required IReadOnlyList<CuteAssignment> Assignments { get; init; }

    /// <summary>The filter, or null to update everything.</summary>
    public CuteExpression? Where { get; init; }
}

/// <summary>A parsed <c>INSERT</c>.</summary>
public sealed class InsertStatement : CuteStatement
{
    /// <summary>The object literals to insert.</summary>
    public required IReadOnlyList<CuteExpression> Documents { get; init; }
}
