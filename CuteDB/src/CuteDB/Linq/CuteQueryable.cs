using System.Collections;
using System.Linq.Expressions;
using CuteDB.Query;

namespace CuteDB.Linq;

/// <summary>A LINQ query over a CuteDB collection.</summary>
/// <typeparam name="T">The element type at this point in the chain.</typeparam>
public sealed class CuteQueryable<T> : IOrderedQueryable<T>
{
    internal CuteQueryable(CuteQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    /// <summary>Creates the root of a query.</summary>
    /// <remarks>
    /// The root expression has to be typed as <c>IQueryable&lt;T&gt;</c>, not as the queryable's own
    /// class and certainly not as <c>object</c>: <see cref="Queryable"/>'s operators bind their
    /// first parameter by type, and anything else fails at the first <c>.Where</c>.
    /// </remarks>
    internal CuteQueryable(CuteQueryProvider provider)
    {
        Provider = provider;
        Expression = Expression.Constant(this, typeof(IQueryable<T>));
    }

    /// <inheritdoc />
    public Type ElementType => typeof(T);

    /// <inheritdoc />
    public Expression Expression { get; }

    /// <inheritdoc />
    public IQueryProvider Provider { get; }

    /// <summary>The provider, typed, for the debugging extensions.</summary>
    internal CuteQueryProvider CuteProvider => (CuteQueryProvider)Provider;

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
        => ((IEnumerable<T>)CuteProvider.Execute(Expression, typeof(IEnumerable<T>))!).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The CuteQL this query runs as. Never executes it.</summary>
    public override string ToString() => CuteProvider.Translate(Expression).ToCuteQL(indented: false);
}

/// <summary>
/// Turns LINQ expression trees into CuteQL and runs them.
/// </summary>
/// <remarks>
/// <para>
/// The whole query is translated into a single statement before anything executes, so filtering,
/// ordering, grouping and paging all happen inside the engine. Nothing is fetched and discarded.
/// </para>
/// <para>
/// The one place work happens in memory is a <c>Select</c> whose body has no CuteQL equivalent;
/// even then the engine has already filtered, ordered and paged, and only the final shaping is
/// done here. Everything else that cannot be translated raises
/// <see cref="CuteTranslationException"/> rather than quietly loading the collection.
/// </para>
/// </remarks>
public sealed class CuteQueryProvider : IQueryProvider
{
    private readonly CuteCollection _collection;
    private readonly Type _documentType;
    private readonly CuteNamingPolicy _naming;

    internal CuteQueryProvider(CuteCollection collection, Type documentType, CuteNamingPolicy naming)
    {
        _collection = collection;
        _documentType = documentType;
        _naming = naming;
    }

    /// <summary>The collection being queried.</summary>
    public CuteCollection Collection => _collection;

    /// <inheritdoc />
    public IQueryable CreateQuery(Expression expression)
    {
        var element = ElementTypeOf(expression.Type);
        var queryable = typeof(CuteQueryable<>).MakeGenericType(element);
        return (IQueryable)Activator.CreateInstance(queryable, this, expression)!;
    }

    /// <inheritdoc />
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new CuteQueryable<TElement>(this, expression);

    /// <inheritdoc />
    public object? Execute(Expression expression) => Execute(expression, expression.Type);

    /// <inheritdoc />
    public TResult Execute<TResult>(Expression expression) => (TResult)Execute(expression, typeof(TResult))!;

    /// <summary>Translates an expression tree without running it.</summary>
    internal CuteQueryModel Translate(Expression expression)
        => new CuteQueryTranslator(_collection.Name, _documentType, _naming).Translate(expression);

    /// <summary>Translates and runs an expression tree.</summary>
    internal object? Execute(Expression expression, Type expected)
    {
        var model = Translate(expression);
        var result = _collection.Database.Execute(CuteQLWriter.Write(model.ToStatement()));
        return Shape(model, result, expected);
    }

    /// <summary>Runs a model and reports what the engine did, for the debugging extensions.</summary>
    internal (object? Value, CuteQueryResult Result) ExecuteWithPlan(Expression expression, Type expected)
    {
        var model = Translate(expression);
        var result = _collection.Database.Execute(CuteQLWriter.Write(model.ToStatement()));
        return (Shape(model, result, expected), result);
    }

    private object? Shape(CuteQueryModel model, CuteQueryResult result, Type expected)
    {
        switch (model.Shape)
        {
            case CuteResultShape.Count:
                return (int)ScalarInt(result);

            case CuteResultShape.LongCount:
                return ScalarInt(result);

            case CuteResultShape.Any:
                return result.Rows.Count > 0;

            case CuteResultShape.All:
                // All(p) was translated to "does anything fail p?"; nothing failing means all pass.
                return result.Rows.Count == 0;

            case CuteResultShape.Sum:
            case CuteResultShape.Average:
            case CuteResultShape.Min:
            case CuteResultShape.Max:
            {
                var scalar = result.Rows.Count > 0 ? result.Rows[0]["value"] : CuteValue.Null;

                // SUM over nothing is zero in LINQ, not null.
                if (scalar.IsNullOrMissing && model.Shape == CuteResultShape.Sum)
                {
                    scalar = CuteValue.Int32(0);
                }

                if (scalar.IsNullOrMissing && Nullable.GetUnderlyingType(expected) is null && expected.IsValueType)
                {
                    throw new InvalidOperationException("Sequence contains no elements");
                }

                return CuteMapper.FromValue(scalar, expected, model.Naming);
            }
        }

        var materialized = Materialize(model, result);

        switch (model.Shape)
        {
            case CuteResultShape.First:
            case CuteResultShape.Last:
            case CuteResultShape.ElementAt:
                return materialized.Count > 0
                    ? materialized[0]
                    : throw new InvalidOperationException("Sequence contains no elements");

            case CuteResultShape.FirstOrDefault:
            case CuteResultShape.LastOrDefault:
            case CuteResultShape.ElementAtOrDefault:
                return materialized.Count > 0 ? materialized[0] : Default(model.ResultType);

            case CuteResultShape.Single:
                return materialized.Count switch
                {
                    1 => materialized[0],
                    0 => throw new InvalidOperationException("Sequence contains no elements"),
                    _ => throw new InvalidOperationException("Sequence contains more than one element"),
                };

            case CuteResultShape.SingleOrDefault:
                return materialized.Count switch
                {
                    1 => materialized[0],
                    0 => Default(model.ResultType),
                    _ => throw new InvalidOperationException("Sequence contains more than one element"),
                };

            default:
                return ToTypedList(materialized, model.ResultType);
        }
    }

    /// <summary>Turns the returned rows into result elements.</summary>
    internal static List<object?> Materialize(CuteQueryModel model, CuteQueryResult result)
    {
        var materialized = new List<object?>(result.Rows.Count);

        if (model.ClientSelector is { } selector)
        {
            // The projection could not be pushed down, so documents come back whole and are
            // shaped here — after the engine did the filtering, ordering and paging.
            var input = model.ClientSelectorInput ?? model.DocumentType;
            foreach (var row in result.Rows)
            {
                var document = CuteMapper.FromValue(CuteValue.Object(row), input, model.Naming);
                materialized.Add(selector.DynamicInvoke(document));
            }

            return materialized;
        }

        var build = model.Materialize
            ?? throw new CuteDbException("The query has no way to build its results. This is a bug in CuteDB.");

        foreach (var row in result.Rows)
        {
            materialized.Add(build(row));
        }

        return materialized;
    }

    private static long ScalarInt(CuteQueryResult result)
        => result.Rows.Count > 0 && result.Rows[0]["value"].IsNumber ? result.Rows[0]["value"].AsInt64 : 0;

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    /// <summary>Copies into a <c>List&lt;T&gt;</c> so the caller gets the element type they asked for.</summary>
    private static object ToTypedList(List<object?> items, Type elementType)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static Type ElementTypeOf(Type type)
    {
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                return argument;
            }
        }

        return typeof(object);
    }
}
