using System.Buffers;

namespace CuteDB.Query;

/// <summary>
/// Named values supplied to a query separately from its text.
/// </summary>
/// <remarks>
/// Parameters exist so that user input never has to be pasted into a query string. A value bound
/// here is used as a value and can never be reinterpreted as syntax, which removes the injection
/// question entirely rather than trying to escape it away.
/// </remarks>
public sealed class CuteParameters
{
    private readonly Dictionary<string, CuteValue> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty parameter set.</summary>
    public CuteParameters()
    {
    }

    /// <summary>Creates a parameter set from name/value pairs.</summary>
    public CuteParameters(IEnumerable<KeyValuePair<string, CuteValue>> values)
    {
        foreach (var (name, value) in values)
        {
            _values[Normalize(name)] = value;
        }
    }

    /// <summary>The number of bound parameters.</summary>
    public int Count => _values.Count;

    /// <summary>Gets or sets a parameter. The leading <c>@</c> or <c>$</c> is optional.</summary>
    public CuteValue this[string name]
    {
        get => _values.TryGetValue(Normalize(name), out var value) ? value : CuteValue.Missing;
        set => _values[Normalize(name)] = value;
    }

    /// <summary>Binds a parameter and returns this set, so calls chain.</summary>
    public CuteParameters Set(string name, CuteValue value)
    {
        _values[Normalize(name)] = value;
        return this;
    }

    /// <summary>True when the parameter has been bound.</summary>
    public bool Contains(string name) => _values.ContainsKey(Normalize(name));

    /// <summary>The bound names, without prefixes.</summary>
    public IEnumerable<string> Names => _values.Keys;

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return name[0] is '@' or '$' ? name[1..] : name;
    }
}

/// <summary>
/// The document an expression is being evaluated against, plus anything else it may need.
/// </summary>
/// <remarks>
/// A row is either still encoded — the normal case during a scan, where nothing has been decoded
/// yet and paths are resolved straight off the bytes — or already materialised, which is what
/// grouping and post-aggregation produce. Keeping both shapes behind one type means the evaluator
/// is written once.
/// </remarks>
public readonly ref struct CuteEvalContext
{
    private readonly ReadOnlySpan<byte> _encoded;
    private readonly CuteObject? _materialized;
    private readonly CuteParameters? _parameters;
    private readonly Dictionary<FunctionExpression, CuteValue>? _aggregates;
    private readonly Dictionary<string, CuteValue>? _groupKeys;

    /// <summary>Creates a context over an encoded document.</summary>
    public CuteEvalContext(ReadOnlySpan<byte> encoded, CuteParameters? parameters = null)
    {
        _encoded = encoded;
        _materialized = null;
        _parameters = parameters;
        _aggregates = null;
        _groupKeys = null;
    }

    /// <summary>Creates a context over a materialised object.</summary>
    public CuteEvalContext(CuteObject materialized, CuteParameters? parameters = null)
    {
        _encoded = default;
        _materialized = materialized;
        _parameters = parameters;
        _aggregates = null;
        _groupKeys = null;
    }

    private CuteEvalContext(
        ReadOnlySpan<byte> encoded,
        CuteObject? materialized,
        CuteParameters? parameters,
        Dictionary<FunctionExpression, CuteValue>? aggregates,
        Dictionary<string, CuteValue>? groupKeys)
    {
        _encoded = encoded;
        _materialized = materialized;
        _parameters = parameters;
        _aggregates = aggregates;
        _groupKeys = groupKeys;
    }

    /// <summary>The bound parameters, if any.</summary>
    public CuteParameters? Parameters => _parameters;

    /// <summary>Returns a copy of this context carrying pre-computed aggregate results.</summary>
    public CuteEvalContext WithAggregates(Dictionary<FunctionExpression, CuteValue> aggregates)
        => new(_encoded, _materialized, _parameters, aggregates, _groupKeys);

    /// <summary>
    /// Returns a copy carrying the group-by values, keyed by the source text of the expression
    /// that produced them.
    /// </summary>
    /// <remarks>
    /// Grouping collapses many documents into one row, so the underlying fields are gone by the
    /// time projections run: <c>SELECT address.city … GROUP BY address.city</c> has no document
    /// left to resolve <c>address.city</c> against. Matching on the expression's text is what
    /// reconnects the projection to the key it was grouped by, and it works the same for a path,
    /// a function call, or any other groupable expression.
    /// </remarks>
    public CuteEvalContext WithGroupKeys(Dictionary<string, CuteValue> groupKeys)
        => new(_encoded, _materialized, _parameters, _aggregates, groupKeys);

    internal bool TryGetGroupKey(CuteExpression expression, out CuteValue value)
    {
        if (_groupKeys is not null)
        {
            return _groupKeys.TryGetValue(expression.ToString(), out value);
        }

        value = CuteValue.Missing;
        return false;
    }

    /// <summary>Resolves a path against the row.</summary>
    public CuteValue Resolve(CutePath path)
        => _materialized is not null
            ? path.Resolve(CuteValue.Object(_materialized))
            : path.ResolveEncoded(_encoded);

    /// <summary>The whole row as a value, decoding it if necessary.</summary>
    public CuteValue AsValue()
        => _materialized is not null ? CuteValue.Object(_materialized) : CuteBinary.Decode(_encoded);

    internal bool TryGetAggregate(FunctionExpression function, out CuteValue value)
    {
        if (_aggregates is not null)
        {
            return _aggregates.TryGetValue(function, out value);
        }

        value = CuteValue.Missing;
        return false;
    }
}

/// <summary>
/// Evaluates a parsed CuteQL expression against one document.
/// </summary>
/// <remarks>
/// <para>
/// This is the reference implementation of CuteQL's semantics. The native accelerator can execute
/// a compiled subset of the same expressions much faster, but anything it declines falls back to
/// here, and the two are held to identical results by the parity tests.
/// </para>
/// <para>
/// The three-valued logic is worth stating: comparing against a missing or null field yields
/// <see cref="CuteValue.Missing"/>, not false, and only <see cref="CuteValue.IsTruthy"/> at the
/// top of a predicate turns that into a rejection. That is what makes <c>NOT (age &gt; 30)</c>
/// exclude documents with no age at all, rather than including them.
/// </para>
/// </remarks>
public static class CuteEvaluator
{
    /// <summary>Evaluates an expression to a value.</summary>
    public static CuteValue Evaluate(CuteExpression expression, scoped in CuteEvalContext context)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return literal.Value;

            case PathExpression path:
                return context.TryGetGroupKey(path, out var groupedPath)
                    ? groupedPath
                    : context.Resolve(path.Path);

            case StarExpression:
                return context.AsValue();

            case ParameterExpression parameter:
            {
                var value = context.Parameters?[parameter.Name] ?? CuteValue.Missing;
                if (value.IsMissing && context.Parameters?.Contains(parameter.Name) != true)
                {
                    throw new CuteDbException($"Parameter @{parameter.Name} was used but never bound.");
                }

                return value;
            }

            case UnaryExpression unary:
                return EvaluateUnary(unary, in context);

            case BinaryExpression binary:
                return EvaluateBinary(binary, in context);

            case BetweenExpression between:
                return EvaluateBetween(between, in context);

            case InExpression inExpression:
                return EvaluateIn(inExpression, in context);

            case IsExpression isExpression:
            {
                var value = Evaluate(isExpression.Value, in context);
                var matches = isExpression.Missing ? value.IsMissing : value.IsNullOrMissing;
                return CuteValue.Boolean(matches != isExpression.Negated);
            }

            case FunctionExpression function:
                return context.TryGetGroupKey(function, out var groupedCall)
                    ? groupedCall
                    : EvaluateFunction(function, in context);

            case ArrayExpression array:
            {
                var items = new CuteArray(array.Items.Count);
                foreach (var item in array.Items)
                {
                    items.Add(Evaluate(item, in context));
                }

                return CuteValue.Array(items);
            }

            case ObjectExpression objectExpression:
            {
                var result = new CuteObject(objectExpression.Fields.Count);
                foreach (var (key, valueExpression) in objectExpression.Fields)
                {
                    var value = Evaluate(valueExpression, in context);

                    // A field whose value does not resolve is left out rather than written as
                    // null, so a projection over sparse documents produces sparse documents.
                    if (!value.IsMissing)
                    {
                        result.Set(key, value);
                    }
                }

                return CuteValue.Object(result);
            }

            default:
                throw new CuteDbException($"Cannot evaluate a {expression.GetType().Name}.");
        }
    }

    /// <summary>Evaluates an expression as a predicate, collapsing missing and null to false.</summary>
    public static bool Test(CuteExpression expression, scoped in CuteEvalContext context)
        => Evaluate(expression, in context).IsTruthy;

    private static CuteValue EvaluateUnary(UnaryExpression unary, scoped in CuteEvalContext context)
    {
        var operand = Evaluate(unary.Operand, in context);
        switch (unary.Operator)
        {
            case CuteUnaryOperator.Not:
                return operand.IsNullOrMissing ? CuteValue.Missing : CuteValue.Boolean(!operand.IsTruthy);

            case CuteUnaryOperator.Negate:
                if (!operand.IsNumber)
                {
                    return CuteValue.Missing;
                }

                return operand.Type switch
                {
                    CuteType.Int32 => CuteValue.Int32(-operand.AsInt32),
                    CuteType.Int64 => CuteValue.Int64(-operand.AsInt64),
                    CuteType.Decimal => CuteValue.Decimal(-operand.AsDecimal),
                    _ => CuteValue.Double(-operand.AsDouble),
                };

            default:
                return CuteValue.Missing;
        }
    }

    private static CuteValue EvaluateBinary(BinaryExpression binary, scoped in CuteEvalContext context)
    {
        // AND and OR short-circuit, which is what makes `city = 'X' AND expensive_function(...)`
        // affordable: the right side is never evaluated for rows the left side already rejected.
        if (binary.Operator == CuteBinaryOperator.And)
        {
            var left = Evaluate(binary.Left, in context);
            if (!left.IsTruthy)
            {
                return CuteValue.Boolean(false);
            }

            return CuteValue.Boolean(Evaluate(binary.Right, in context).IsTruthy);
        }

        if (binary.Operator == CuteBinaryOperator.Or)
        {
            var left = Evaluate(binary.Left, in context);
            if (left.IsTruthy)
            {
                return CuteValue.Boolean(true);
            }

            return CuteValue.Boolean(Evaluate(binary.Right, in context).IsTruthy);
        }

        var a = Evaluate(binary.Left, in context);
        var b = Evaluate(binary.Right, in context);

        switch (binary.Operator)
        {
            case CuteBinaryOperator.Equal:
            case CuteBinaryOperator.NotEqual:
            case CuteBinaryOperator.Less:
            case CuteBinaryOperator.LessOrEqual:
            case CuteBinaryOperator.Greater:
            case CuteBinaryOperator.GreaterOrEqual:
                return Compare(binary.Operator, a, b);

            case CuteBinaryOperator.Like:
                return a.IsNullOrMissing ? CuteValue.Missing : CuteValue.Boolean(CuteFunctions.Like(a, b));

            case CuteBinaryOperator.NotLike:
                return a.IsNullOrMissing ? CuteValue.Missing : CuteValue.Boolean(!CuteFunctions.Like(a, b));

            default:
                return Arithmetic(binary.Operator, a, b);
        }
    }

    private static CuteValue Compare(CuteBinaryOperator op, CuteValue a, CuteValue b)
    {
        // Comparing against an absent field has no true answer, so the result is missing and the
        // predicate rejects the row without claiming the comparison was false.
        if (a.IsMissing || b.IsMissing)
        {
            return CuteValue.Missing;
        }

        if (a.IsNull || b.IsNull)
        {
            // Null equals null and differs from everything else. Ordering against null is not
            // meaningful in either direction, so the four ordering operators answer "unknown"
            // rather than picking a side.
            if (op is not (CuteBinaryOperator.Equal or CuteBinaryOperator.NotEqual))
            {
                return CuteValue.Missing;
            }

            var bothNull = a.IsNull && b.IsNull;
            return CuteValue.Boolean(op == CuteBinaryOperator.Equal ? bothNull : !bothNull);
        }

        // A field holding an array, compared against a scalar, matches when *any* element does.
        // Without this, an index on `tags` would be unusable: it indexes each element, so it hands
        // back exactly the documents whose array contains the value, and a whole-array comparison
        // would then reject every one of them. It is also what people mean by
        // `WHERE tags = 'promo'`. Comparing an array against an array stays a whole-value
        // comparison, so `tags = ['promo','bulk']` still means what it says.
        if (a.IsArray != b.IsArray)
        {
            var array = a.IsArray ? a.AsArray : b.AsArray;
            var scalar = a.IsArray ? b : a;
            var arrayOnLeft = a.IsArray;

            // NOT EQUAL over an array means "no element equals", not "some element differs".
            if (op == CuteBinaryOperator.NotEqual)
            {
                foreach (var element in array.AsSpan())
                {
                    if (CuteValueComparer.Equal(element, scalar))
                    {
                        return CuteValue.Boolean(false);
                    }
                }

                return CuteValue.Boolean(true);
            }

            foreach (var element in array.AsSpan())
            {
                var elementOrder = arrayOnLeft
                    ? CuteValueComparer.Compare(element, scalar)
                    : CuteValueComparer.Compare(scalar, element);

                if (Satisfies(op, elementOrder))
                {
                    return CuteValue.Boolean(true);
                }
            }

            return CuteValue.Boolean(false);
        }

        return CuteValue.Boolean(Satisfies(op, CuteValueComparer.Compare(a, b)));
    }

    private static bool Satisfies(CuteBinaryOperator op, int order) => op switch
    {
        CuteBinaryOperator.Equal => order == 0,
        CuteBinaryOperator.NotEqual => order != 0,
        CuteBinaryOperator.Less => order < 0,
        CuteBinaryOperator.LessOrEqual => order <= 0,
        CuteBinaryOperator.Greater => order > 0,
        CuteBinaryOperator.GreaterOrEqual => order >= 0,
        _ => false,
    };

    private static CuteValue Arithmetic(CuteBinaryOperator op, CuteValue a, CuteValue b)
    {
        // '+' doubles as string concatenation when either side is text, which is what people
        // reach for and costs nothing to support.
        if (op == CuteBinaryOperator.Add && (a.Type == CuteType.String || b.Type == CuteType.String))
        {
            if (a.IsNullOrMissing || b.IsNullOrMissing)
            {
                return CuteValue.Missing;
            }

            return CuteValue.String(a.ToDisplayString() + b.ToDisplayString());
        }

        if (!a.IsNumber || !b.IsNumber)
        {
            return CuteValue.Missing;
        }

        var integral = a.Type is CuteType.Int32 or CuteType.Int64 && b.Type is CuteType.Int32 or CuteType.Int64;
        var exact = a.Type == CuteType.Decimal || b.Type == CuteType.Decimal;

        switch (op)
        {
            case CuteBinaryOperator.Add:
                return integral ? CuteValue.Int64(a.AsInt64 + b.AsInt64)
                    : exact ? CuteValue.Decimal(AsDecimal(a) + AsDecimal(b))
                    : CuteValue.Double(a.AsDouble + b.AsDouble);

            case CuteBinaryOperator.Subtract:
                return integral ? CuteValue.Int64(a.AsInt64 - b.AsInt64)
                    : exact ? CuteValue.Decimal(AsDecimal(a) - AsDecimal(b))
                    : CuteValue.Double(a.AsDouble - b.AsDouble);

            case CuteBinaryOperator.Multiply:
                return integral ? CuteValue.Int64(a.AsInt64 * b.AsInt64)
                    : exact ? CuteValue.Decimal(AsDecimal(a) * AsDecimal(b))
                    : CuteValue.Double(a.AsDouble * b.AsDouble);

            case CuteBinaryOperator.Divide:
            {
                // Integer division that does not divide evenly widens rather than truncating:
                // 7 / 2 is 3.5, because a query language that silently loses the remainder is a
                // reporting bug waiting to happen.
                if (exact)
                {
                    var divisor = AsDecimal(b);
                    return divisor == 0m ? CuteValue.Missing : CuteValue.Decimal(AsDecimal(a) / divisor);
                }

                var right = b.AsDouble;
                return right == 0 ? CuteValue.Missing : CuteValue.Double(a.AsDouble / right);
            }

            case CuteBinaryOperator.Modulo:
            {
                if (integral)
                {
                    var divisor = b.AsInt64;
                    return divisor == 0 ? CuteValue.Missing : CuteValue.Int64(a.AsInt64 % divisor);
                }

                var right = b.AsDouble;
                return right == 0 ? CuteValue.Missing : CuteValue.Double(a.AsDouble % right);
            }

            default:
                return CuteValue.Missing;
        }
    }

    private static decimal AsDecimal(CuteValue value)
        => value.Type == CuteType.Decimal ? value.AsDecimal : (decimal)value.AsDouble;

    private static CuteValue EvaluateBetween(BetweenExpression between, scoped in CuteEvalContext context)
    {
        var value = Evaluate(between.Value, in context);
        if (value.IsNullOrMissing)
        {
            return CuteValue.Missing;
        }

        var low = Evaluate(between.Low, in context);
        var high = Evaluate(between.High, in context);
        if (low.IsNullOrMissing || high.IsNullOrMissing)
        {
            return CuteValue.Missing;
        }

        var inRange = CuteValueComparer.Compare(value, low) >= 0 && CuteValueComparer.Compare(value, high) <= 0;
        return CuteValue.Boolean(inRange != between.Negated);
    }

    private static CuteValue EvaluateIn(InExpression expression, scoped in CuteEvalContext context)
    {
        var value = Evaluate(expression.Value, in context);
        if (value.IsMissing)
        {
            return CuteValue.Missing;
        }

        var found = false;
        foreach (var itemExpression in expression.Items)
        {
            var item = Evaluate(itemExpression, in context);

            // `x IN @list` binds a single parameter holding an array, so an array on the right
            // means "any of these" rather than "equal to this array".
            if (item.IsArray && itemExpression is ParameterExpression)
            {
                foreach (var candidate in item.AsArray.AsSpan())
                {
                    if (Compare(CuteBinaryOperator.Equal, value, candidate).IsTruthy)
                    {
                        found = true;
                        break;
                    }
                }
            }
            else if (Compare(CuteBinaryOperator.Equal, value, item).IsTruthy)
            {
                // Deliberately the same comparison `=` uses rather than raw value equality, so
                // that `tags IN ('promo','bulk')` matches an array field element-wise exactly the
                // way `tags = 'promo'` does. The two spellings meaning different things would be
                // a trap.
                found = true;
            }

            if (found)
            {
                break;
            }
        }

        return CuteValue.Boolean(found != expression.Negated);
    }

    private static CuteValue EvaluateFunction(FunctionExpression function, scoped in CuteEvalContext context)
    {
        if (function.IsAggregate)
        {
            // Aggregates are computed once per group by the executor and handed back through the
            // context; reaching one here without a precomputed value means it was used somewhere
            // grouping does not apply, such as a bare WHERE clause.
            if (context.TryGetAggregate(function, out var aggregated))
            {
                return aggregated;
            }

            throw new CuteDbException(
                $"{function.Name} is an aggregate and can only appear in SELECT or HAVING, not in WHERE.");
        }

        if (function.Name == "NOW")
        {
            return CuteFunctions.Invoke("NOW", ReadOnlySpan<CuteValue>.Empty);
        }

        // EXISTS has to see a genuinely missing value, which every other path here would have
        // already turned into an argument, so it is evaluated the same way — the distinction is
        // preserved because Resolve returns Missing rather than throwing.
        // CuteValue holds an object reference, so the argument buffer cannot be stack-allocated.
        // Renting keeps a function call inside a scan from allocating once per row.
        var count = function.Arguments.Count;
        var buffer = ArrayPool<CuteValue>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                buffer[i] = Evaluate(function.Arguments[i], in context);
            }

            return CuteFunctions.Invoke(function.Name, buffer.AsSpan(0, count));
        }
        finally
        {
            // Clear on return: the buffer holds references that would otherwise stay reachable
            // through the pool for as long as the process runs.
            ArrayPool<CuteValue>.Shared.Return(buffer, clearArray: true);
        }
    }
}
