using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using CuteDB.Mapping;
using CuteDB.Query;

namespace CuteDB.Linq;

/// <summary>Raised when a LINQ expression has no CuteQL equivalent.</summary>
/// <remarks>
/// The message names the construct and, where there is one, the shape that does work. A provider
/// that fails with "the LINQ expression could not be translated" and nothing else is a provider
/// people give up on.
/// </remarks>
public sealed class CuteTranslationException(string message) : CuteDbException(message);

/// <summary>
/// Turns the body of a LINQ lambda into a CuteQL expression.
/// </summary>
/// <remarks>
/// <para>
/// Two things happen here that are worth knowing about. Any subtree that does not mention the
/// lambda's parameter is <em>evaluated</em> rather than translated, so a captured local, a field
/// on <c>this</c>, or a call like <c>DateTime.UtcNow.AddDays(-7)</c> becomes a constant before it
/// ever reaches CuteQL. And a member chain becomes a document path, so <c>o.Customer.Address.City</c>
/// translates to <c>customer.address.city</c> using each type's own naming policy.
/// </para>
/// <para>
/// Everything it cannot translate raises <see cref="CuteTranslationException"/> naming the
/// construct. Silently fetching the whole collection and filtering in memory — what several ORMs
/// do — turns a missing translation into a production incident rather than a compile-time-ish
/// error.
/// </para>
/// </remarks>
internal sealed class CuteExpressionTranslator(
    CuteNamingPolicy naming,
    IReadOnlyCollection<System.Linq.Expressions.ParameterExpression> parameters,
    IReadOnlyDictionary<string, CuteExpression>? projectionAliases = null)
{
    private readonly HashSet<System.Linq.Expressions.ParameterExpression> _parameters = [.. parameters];

    /// <summary>Translates an expression, unwrapping quotes and conversions.</summary>
    internal CuteExpression Translate(Expression expression)
    {
        expression = Strip(expression);

        // A filter written after a GroupBy talks about the grouping — g.Count(), g.Key — which
        // only the query translator knows how to read, so it gets first refusal.
        if (GroupResolver?.Invoke(expression) is { } resolved)
        {
            return resolved;
        }

        // Anything not mentioning the lambda parameter is a value, not a query fragment.
        if (!MentionsParameter(expression))
        {
            return new LiteralExpression(ToValue(Evaluate(expression)));
        }

        return expression switch
        {
            MemberExpression member => TranslateMember(member),
            System.Linq.Expressions.BinaryExpression binary => TranslateBinary(binary),
            System.Linq.Expressions.UnaryExpression unary => TranslateUnary(unary),
            MethodCallExpression call => TranslateCall(call),
            ConditionalExpression conditional => TranslateConditional(conditional),
            // `.Select(o => o.City).OrderBy(c => c)`: after a projection to a single value, the
            // parameter *is* that value, and the projection recorded it under `value`.
            System.Linq.Expressions.ParameterExpression
                when projectionAliases?.TryGetValue("value", out var scalar) is true => scalar,
            System.Linq.Expressions.ParameterExpression => throw new CuteTranslationException(
                "The whole document cannot be used as a value here. Compare a property of it instead."),
            _ => throw new CuteTranslationException(
                $"CuteQL has no equivalent for {expression.NodeType} in this position."),
        };
    }

    /// <summary>Translates a predicate, ensuring the result reads as a condition.</summary>
    internal CuteExpression TranslatePredicate(Expression expression)
    {
        var translated = Translate(expression);

        // `.Where(o => o.Paid)` gives a bare path; CuteQL is happy to treat a value as a condition,
        // but comparing explicitly is clearer in the rendered query and behaves identically.
        if (translated is PathExpression or FunctionExpression)
        {
            return new Query.BinaryExpression(
                CuteBinaryOperator.Equal,
                translated,
                new LiteralExpression(CuteValue.Boolean(true)));
        }

        return translated;
    }

    /// <summary>
    /// Consulted before anything else, so a filter over a grouping can turn <c>g.Count()</c> into
    /// <c>COUNT(*)</c>. Returns null for a subtree it does not recognise.
    /// </summary>
    internal Func<Expression, CuteExpression?>? GroupResolver { get; set; }

    /// <summary>The document path a member chain refers to, or null when it is not one.</summary>
    /// <remarks>
    /// The chain is walked to its root <em>before</em> any type is mapped. Mapping first was a
    /// bug worth remembering: a captured local reaches the translator as a field on a
    /// compiler-generated closure class, and building a type map for one compiles a member
    /// accessor against a private type, which the runtime rejects with InvalidProgramException.
    /// </remarks>
    internal string? TryGetPath(Expression expression)
    {
        expression = Strip(expression);
        if (expression is not MemberExpression)
        {
            return null;
        }

        // Walk to the root first. Only a chain rooted at this lambda's own parameter is a path.
        var chain = new List<MemberExpression>(4);
        Expression? current = expression;

        while (current is MemberExpression step)
        {
            chain.Insert(0, step);
            current = Strip(step.Expression!);
        }

        if (current is not System.Linq.Expressions.ParameterExpression parameter || !_parameters.Contains(parameter))
        {
            return null;
        }

        var segments = new List<string>(chain.Count);
        foreach (var step in chain)
        {
            var declaring = step.Member.DeclaringType;

            // A member of a scalar — DateTime.Year, string.Length — is a function, not a path
            // segment, and mapping it would silently produce nonsense like `placedAt.year`.
            if (declaring is null || IsScalar(declaring))
            {
                return null;
            }

            var mapped = CuteTypeMap.For(declaring, naming).ByClrName(step.Member.Name);
            if (mapped is null)
            {
                return null;
            }

            segments.Add(mapped.FieldName);
        }

        return string.Join('.', segments);
    }

    /// <summary>True for types whose members are values rather than sub-documents.</summary>
    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan)
            || underlying == typeof(Guid)
            || underlying == typeof(CuteId);
    }

    private CuteExpression TranslateMember(MemberExpression member)
    {
        // After a Select was pushed down, a later Where or OrderBy is written against the
        // projected shape. Resolving the alias back to the expression it stands for lets the
        // engine keep evaluating it, rather than forcing the filter into memory.
        if (projectionAliases is not null
            && member.Expression is System.Linq.Expressions.ParameterExpression parameter
            && _parameters.Contains(parameter)
            && projectionAliases.TryGetValue(member.Member.Name, out var aliased))
        {
            return aliased;
        }

        if (TryGetPath(member) is { } path)
        {
            return new PathExpression(CutePath.Parse(path));
        }

        // Not a mapped path: a property on a framework type that CuteQL has a function for.
        var declaring = member.Member.DeclaringType;

        if (declaring == typeof(string) && member.Member.Name == nameof(string.Length))
        {
            return Function("LENGTH", Translate(member.Expression!));
        }

        if (declaring == typeof(DateTime) || declaring == typeof(DateTimeOffset))
        {
            var target = Translate(member.Expression!);
            return member.Member.Name switch
            {
                nameof(DateTime.Year) => Function("YEAR", target),
                nameof(DateTime.Month) => Function("MONTH", target),
                nameof(DateTime.Day) => Function("DAY", target),
                nameof(DateTime.Hour) => Function("HOUR", target),
                nameof(DateTime.Minute) => Function("DATE_PART", Literal("MINUTE"), target),
                nameof(DateTime.Second) => Function("DATE_PART", Literal("SECOND"), target),
                nameof(DateTime.DayOfYear) => Function("DATE_PART", Literal("DAYOFYEAR"), target),
                nameof(DateTime.DayOfWeek) => Function("DATE_PART", Literal("DAYOFWEEK"), target),
                nameof(DateTime.Date) => Function("DATE_TRUNC", Literal("DAY"), target),
                _ => throw new CuteTranslationException(
                    $"DateTime.{member.Member.Name} has no CuteQL equivalent. " +
                    "Year, Month, Day, Hour, Minute, Second, DayOfYear, DayOfWeek and Date do."),
            };
        }

        if (IsCollectionCount(member))
        {
            return Function("LENGTH", Translate(member.Expression!));
        }

        throw new CuteTranslationException(
            $"'{declaring?.Name}.{member.Member.Name}' is not a stored field and has no CuteQL equivalent. " +
            "Map it with [CuteField], or compute it after the query.");
    }

    private static bool IsCollectionCount(MemberExpression member)
        => member.Member.Name is "Count" or "Length"
            && member.Expression is not null
            && member.Expression.Type != typeof(string)
            && typeof(IEnumerable).IsAssignableFrom(member.Expression.Type);

    private CuteExpression TranslateBinary(System.Linq.Expressions.BinaryExpression binary)
    {
        // Coalesce maps onto COALESCE rather than onto a comparison.
        if (binary.NodeType == ExpressionType.Coalesce)
        {
            return Function("COALESCE", Translate(binary.Left), Translate(binary.Right));
        }

        var op = binary.NodeType switch
        {
            ExpressionType.Equal => CuteBinaryOperator.Equal,
            ExpressionType.NotEqual => CuteBinaryOperator.NotEqual,
            ExpressionType.LessThan => CuteBinaryOperator.Less,
            ExpressionType.LessThanOrEqual => CuteBinaryOperator.LessOrEqual,
            ExpressionType.GreaterThan => CuteBinaryOperator.Greater,
            ExpressionType.GreaterThanOrEqual => CuteBinaryOperator.GreaterOrEqual,
            ExpressionType.AndAlso or ExpressionType.And => CuteBinaryOperator.And,
            ExpressionType.OrElse or ExpressionType.Or => CuteBinaryOperator.Or,
            ExpressionType.Add or ExpressionType.AddChecked => CuteBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => CuteBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => CuteBinaryOperator.Multiply,
            ExpressionType.Divide => CuteBinaryOperator.Divide,
            ExpressionType.Modulo => CuteBinaryOperator.Modulo,
            _ => throw new CuteTranslationException($"CuteQL has no operator for {binary.NodeType}."),
        };

        // `o.Status == OrderStatus.Shipped` compiles to an int comparison, so by the time the
        // constant is evaluated its enum identity is gone and it would render as `status = 2`.
        // Enums are stored by name, so the original type has to be recovered here.
        var enumType = EnumTypeOf(binary.Left) ?? EnumTypeOf(binary.Right);

        var left = TranslateOperand(binary.Left, enumType);
        var right = TranslateOperand(binary.Right, enumType);

        // Comparing against null is an existence question in a schemaless store, and IS NULL is
        // what expresses it. `x == null` written as `x = NULL` would be unknown for every row.
        if (op is CuteBinaryOperator.Equal or CuteBinaryOperator.NotEqual)
        {
            if (IsNullLiteral(right))
            {
                return new IsExpression(left, op == CuteBinaryOperator.NotEqual, missing: false);
            }

            if (IsNullLiteral(left))
            {
                return new IsExpression(right, op == CuteBinaryOperator.NotEqual, missing: false);
            }
        }

        return new Query.BinaryExpression(op, left, right);
    }

    /// <summary>Translates one side of a comparison, restoring an enum constant's identity.</summary>
    private CuteExpression TranslateOperand(Expression expression, Type? enumType)
    {
        if (enumType is not null && !MentionsParameter(expression))
        {
            var raw = Evaluate(expression);
            if (raw is not null)
            {
                var asEnum = raw.GetType().IsEnum ? raw : Enum.ToObject(enumType, raw);
                return new LiteralExpression(CuteValue.String(asEnum.ToString()!));
            }
        }

        return Translate(expression);
    }

    /// <summary>The enum type behind an expression, seen through the compiler's int conversion.</summary>
    private static Type? EnumTypeOf(Expression expression)
    {
        var type = expression.Type;
        if (type.IsEnum)
        {
            return type;
        }

        if (Nullable.GetUnderlyingType(type) is { IsEnum: true } nullable)
        {
            return nullable;
        }

        return expression is System.Linq.Expressions.UnaryExpression
        {
            NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
        } unary
            ? EnumTypeOf(unary.Operand)
            : null;
    }

    private CuteExpression TranslateUnary(System.Linq.Expressions.UnaryExpression unary) => unary.NodeType switch
    {
        ExpressionType.Not => new Query.UnaryExpression(CuteUnaryOperator.Not, Translate(unary.Operand)),
        ExpressionType.Negate or ExpressionType.NegateChecked =>
            new Query.UnaryExpression(CuteUnaryOperator.Negate, Translate(unary.Operand)),
        ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote =>
            Translate(unary.Operand),
        ExpressionType.TypeAs => Translate(unary.Operand),
        _ => throw new CuteTranslationException($"CuteQL has no equivalent for the unary {unary.NodeType}."),
    };

    private CuteExpression TranslateConditional(ConditionalExpression conditional)
        => throw new CuteTranslationException(
            "A conditional (?:) has no CuteQL equivalent. Use COALESCE through '??', or compute it after the query.");

    private CuteExpression TranslateCall(MethodCallExpression call)
    {
        var name = call.Method.Name;
        var declaring = call.Method.DeclaringType;

        // ---- string ------------------------------------------------------------------------
        if (declaring == typeof(string))
        {
            switch (name)
            {
                case nameof(string.StartsWith) when call.Arguments.Count >= 1:
                    return Like(call.Object!, call.Arguments[0], prefix: false, suffix: true);

                case nameof(string.EndsWith) when call.Arguments.Count >= 1:
                    return Like(call.Object!, call.Arguments[0], prefix: true, suffix: false);

                case nameof(string.Contains) when call.Object is not null && call.Arguments.Count >= 1:
                    return Like(call.Object, call.Arguments[0], prefix: true, suffix: true);

                case nameof(string.ToUpper) or nameof(string.ToUpperInvariant):
                    return Function("UPPER", Translate(call.Object!));

                case nameof(string.ToLower) or nameof(string.ToLowerInvariant):
                    return Function("LOWER", Translate(call.Object!));

                case nameof(string.Trim) when call.Arguments.Count == 0:
                    return Function("TRIM", Translate(call.Object!));

                case nameof(string.Substring):
                    return call.Arguments.Count == 1
                        ? Function("SUBSTR", Translate(call.Object!), Translate(call.Arguments[0]))
                        : Function("SUBSTR", Translate(call.Object!), Translate(call.Arguments[0]), Translate(call.Arguments[1]));

                case nameof(string.Replace) when call.Arguments.Count >= 2:
                    return Function("REPLACE", Translate(call.Object!), Translate(call.Arguments[0]), Translate(call.Arguments[1]));

                case nameof(string.Concat):
                    return Function("CONCAT", call.Arguments.Select(Translate).ToArray());

                case nameof(string.IsNullOrEmpty) when call.Arguments.Count == 1:
                {
                    var target = Translate(call.Arguments[0]);
                    return new Query.BinaryExpression(
                        CuteBinaryOperator.Or,
                        new IsExpression(target, negated: false, missing: false),
                        new Query.BinaryExpression(CuteBinaryOperator.Equal, Function("LENGTH", target), Literal(0)));
                }
            }
        }

        // ---- Math --------------------------------------------------------------------------
        if (declaring == typeof(Math))
        {
            var mapped = name switch
            {
                nameof(Math.Abs) => "ABS",
                nameof(Math.Round) => "ROUND",
                nameof(Math.Floor) => "FLOOR",
                nameof(Math.Ceiling) => "CEIL",
                nameof(Math.Sqrt) => "SQRT",
                nameof(Math.Pow) => "POW",
                _ => null,
            };

            if (mapped is not null)
            {
                return Function(mapped, call.Arguments.Select(Translate).ToArray());
            }
        }

        // ---- Contains: membership, either way round ----------------------------------------
        if (name == nameof(Enumerable.Contains))
        {
            var (source, item) = call.Object is not null
                ? (call.Object, call.Arguments[0])
                : (call.Arguments[0], call.Arguments[1]);

            // A stored array field: `o.Tags.Contains("promo")`. CuteQL compares an array field
            // element-wise, so equality already means "contains".
            if (TryGetPath(source) is { } path)
            {
                return new Query.BinaryExpression(
                    CuteBinaryOperator.Equal,
                    new PathExpression(CutePath.Parse(path)),
                    Translate(item));
            }

            // A local collection: `codes.Contains(o.Code)` becomes IN.
            if (!MentionsParameter(source))
            {
                var values = Evaluate(source) as IEnumerable
                    ?? throw new CuteTranslationException("Contains needs a collection to search.");

                var items = new List<CuteExpression>();
                foreach (var value in values)
                {
                    items.Add(new LiteralExpression(ToValue(value)));
                }

                return items.Count == 0
                    // `IN ()` is not valid and would be a syntax error; an always-false condition
                    // is what an empty set means.
                    ? new Query.BinaryExpression(CuteBinaryOperator.Equal, Literal(0), Literal(1))
                    : new InExpression(Translate(item), items, negated: false);
            }
        }

        // ---- Enumerable.Any / All / Count over a stored array ------------------------------
        if (declaring == typeof(Enumerable) && call.Arguments.Count >= 1 && TryGetPath(call.Arguments[0]) is { } arrayPath)
        {
            switch (name)
            {
                case nameof(Enumerable.Count) when call.Arguments.Count == 1:
                    return Function("ARRAY_LENGTH", new PathExpression(CutePath.Parse(arrayPath)));

                case nameof(Enumerable.Any) when call.Arguments.Count == 1:
                    return new Query.BinaryExpression(
                        CuteBinaryOperator.Greater,
                        Function("ARRAY_LENGTH", new PathExpression(CutePath.Parse(arrayPath))),
                        Literal(0));

                case nameof(Enumerable.Any) when call.Arguments.Count == 2:
                {
                    // `o.Lines.Any(l => l.Sku == "X")` is exactly what a projecting path means:
                    // the predicate is rewritten against `lines[]` and compared element-wise.
                    var lambda = (LambdaExpression)Strip(call.Arguments[1]);
                    var inner = new CuteExpressionTranslator(naming, [lambda.Parameters[0]]);
                    return inner.RewriteOntoProjection(lambda.Body, arrayPath);
                }
            }
        }

        // ---- a captured call that does not mention the parameter ---------------------------
        if (!MentionsParameter(call))
        {
            return new LiteralExpression(ToValue(Evaluate(call)));
        }

        throw new CuteTranslationException(
            $"'{declaring?.Name}.{name}' has no CuteQL equivalent. " +
            "Supported: string StartsWith/EndsWith/Contains/ToUpper/ToLower/Trim/Substring/Replace/IsNullOrEmpty, " +
            "Math Abs/Round/Floor/Ceiling/Sqrt/Pow, DateTime parts, Contains for membership, " +
            "and Any/Count over a stored array.");
    }

    /// <summary>
    /// Rewrites a predicate written against an array element so it reads against the array itself.
    /// </summary>
    /// <remarks>
    /// <c>o.Lines.Any(l =&gt; l.Sku == "X")</c> becomes <c>lines[].sku = 'X'</c>. The projecting
    /// path resolves to every line's SKU and CuteQL compares element-wise, so the result is "any
    /// line matches" — the same question, without a join.
    /// </remarks>
    private CuteExpression RewriteOntoProjection(Expression body, string arrayPath)
    {
        var rewritten = Translate(body);
        return PrefixPaths(rewritten, arrayPath);
    }

    private static CuteExpression PrefixPaths(CuteExpression expression, string arrayPath) => expression switch
    {
        PathExpression path => new PathExpression(CutePath.Parse($"{arrayPath}[].{path.Path.Text}")),
        Query.BinaryExpression binary => new Query.BinaryExpression(
            binary.Operator, PrefixPaths(binary.Left, arrayPath), PrefixPaths(binary.Right, arrayPath)),
        Query.UnaryExpression unary => new Query.UnaryExpression(
            unary.Operator, PrefixPaths(unary.Operand, arrayPath)),
        BetweenExpression between => new BetweenExpression(
            PrefixPaths(between.Value, arrayPath),
            PrefixPaths(between.Low, arrayPath),
            PrefixPaths(between.High, arrayPath),
            between.Negated),
        InExpression inExpression => new InExpression(
            PrefixPaths(inExpression.Value, arrayPath), inExpression.Items, inExpression.Negated),
        IsExpression isExpression => new IsExpression(
            PrefixPaths(isExpression.Value, arrayPath), isExpression.Negated, isExpression.Missing),
        FunctionExpression function => new FunctionExpression(
            function.Name, [.. function.Arguments.Select(a => PrefixPaths(a, arrayPath))]),
        _ => expression,
    };

    private CuteExpression Like(Expression target, Expression pattern, bool prefix, bool suffix)
    {
        if (MentionsParameter(pattern))
        {
            throw new CuteTranslationException(
                "The text searched for must be a constant or a captured value, not another field.");
        }

        var text = Evaluate(pattern)?.ToString() ?? string.Empty;

        // The user's text is data, not pattern syntax: a product code containing '%' must match
        // itself rather than becoming a wildcard.
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        var glob = $"{(prefix ? "%" : string.Empty)}{escaped}{(suffix ? "%" : string.Empty)}";
        return new Query.BinaryExpression(CuteBinaryOperator.Like, Translate(target), Literal(glob));
    }

    // -------------------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------------------

    internal bool MentionsParameter(Expression expression) => ParameterFinder.Contains(expression, _parameters);

    internal static Expression Strip(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case System.Linq.Expressions.UnaryExpression
                {
                    NodeType: ExpressionType.Quote or ExpressionType.Convert or ExpressionType.ConvertChecked,
                } unary when unary.Type != typeof(object) || unary.Operand.Type.IsClass:
                    expression = unary.Operand;
                    continue;

                // `array.Contains(x)` binds to MemoryExtensions on a span, so the array arrives
                // wrapped in op_Implicit. Left in place it reaches the compiler as a lambda
                // returning a ref struct, which fails at emit with InvalidProgramException.
                case MethodCallExpression { Object: null, Arguments.Count: 1 } conversion
                    when conversion.Method is { IsSpecialName: true, Name: "op_Implicit" or "op_Explicit" }
                        && IsSpan(conversion.Type):
                    expression = conversion.Arguments[0];
                    continue;

                default:
                    return expression;
            }
        }
    }

    /// <summary>True for <c>Span&lt;T&gt;</c> and <c>ReadOnlySpan&lt;T&gt;</c>.</summary>
    private static bool IsSpan(Type type)
        => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>)
                || type.GetGenericTypeDefinition() == typeof(Span<>));

    /// <summary>Evaluates a subtree that does not depend on the query, turning it into a value.</summary>
    internal static object? Evaluate(Expression expression)
    {
        // The common cases without paying for a compile: a literal, and a captured local, which
        // the compiler turns into a field on a closure object.
        switch (Strip(expression))
        {
            case ConstantExpression constant:
                return constant.Value;

            case MemberExpression { Expression: ConstantExpression owner } member:
                return member.Member switch
                {
                    FieldInfo field => field.GetValue(owner.Value),
                    PropertyInfo property => property.GetValue(owner.Value),
                    _ => Compile(expression),
                };

            default:
                return Compile(expression);
        }
    }

    private static object? Compile(Expression expression)
        => Expression.Lambda(Expression.Convert(expression, typeof(object))).Compile().DynamicInvoke();

    internal static CuteValue ToValue(object? value)
        => value is null ? CuteValue.Null : CuteMapper.ToValue(value, value.GetType(), CuteMapper.DefaultNaming);

    private static bool IsNullLiteral(CuteExpression expression)
        => expression is LiteralExpression { Value.Type: CuteType.Null };

    private static LiteralExpression Literal(object value) => new(ToValue(value));

    private static FunctionExpression Function(string name, params CuteExpression[] arguments)
        => new(name, arguments);

    /// <summary>Reports whether an expression mentions any of the given parameters.</summary>
    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly HashSet<System.Linq.Expressions.ParameterExpression> _wanted;
        private bool _found;

        private ParameterFinder(HashSet<System.Linq.Expressions.ParameterExpression> wanted) => _wanted = wanted;

        internal static bool Contains(Expression expression, HashSet<System.Linq.Expressions.ParameterExpression> wanted)
        {
            var finder = new ParameterFinder(wanted);
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(System.Linq.Expressions.ParameterExpression node)
        {
            if (_wanted.Contains(node))
            {
                _found = true;
            }

            return node;
        }
    }
}
