using System.Linq.Expressions;
using CuteDB.Query;

namespace CuteDB.Linq;

/// <summary>What the caller expects back from a translated query.</summary>
internal enum CuteResultShape
{
    Sequence,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Last,
    LastOrDefault,
    ElementAt,
    ElementAtOrDefault,
    Count,
    LongCount,
    Any,
    All,
    Sum,
    Average,
    Min,
    Max,
}

/// <summary>
/// A LINQ query, translated but not yet run.
/// </summary>
/// <remarks>
/// Holding this as a value rather than executing as it translates is what makes
/// <see cref="CuteQueryableExtensions.ToCuteQL{T}"/> possible: the same model renders to text for
/// inspection and runs against the engine, so what you debug is what executes.
/// </remarks>
internal sealed class CuteQueryModel
{
    internal required string Collection { get; init; }

    /// <summary>The document type the collection is being read as.</summary>
    internal required Type DocumentType { get; init; }

    internal CuteNamingPolicy Naming { get; init; } = CuteNamingPolicy.CamelCase;

    internal CuteExpression? Where { get; set; }

    internal List<CuteOrdering> OrderBy { get; } = [];

    /// <summary>Empty means <c>SELECT *</c>.</summary>
    internal List<CuteProjection> Projections { get; } = [];

    internal List<CuteExpression> GroupBy { get; } = [];

    internal CuteExpression? Having { get; set; }

    internal int? Limit { get; set; }

    internal int Offset { get; set; }

    internal bool Distinct { get; set; }

    internal CuteResultShape Shape { get; set; } = CuteResultShape.Sequence;

    /// <summary>The element type the caller ends up with.</summary>
    internal Type ResultType { get; set; } = typeof(object);

    /// <summary>Builds one result element from a returned row.</summary>
    internal Func<CuteObject, object?>? Materialize { get; set; }

    /// <summary>
    /// A projection that could not be pushed to the engine, applied after mapping instead.
    /// </summary>
    /// <remarks>
    /// Only reached for a selector whose body CuteQL has no equivalent for — a constructor call, a
    /// conditional, a method on a type the translator does not know. The rows still come back
    /// filtered, sorted and paged by the engine; only the final shaping happens here.
    /// </remarks>
    internal Delegate? ClientSelector { get; set; }

    /// <summary>Alias to expression, so a Where or OrderBy after a Select can be inlined.</summary>
    internal Dictionary<string, CuteExpression> ProjectionAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// For a composite GroupBy key, the member names in the same order as <see cref="GroupBy"/>,
    /// so that <c>g.Key.City</c> can be resolved back to the expression it was grouped by.
    /// </summary>
    internal List<string> GroupKeyMembers { get; set; } = [];

    /// <summary>The CLR type of the grouping key.</summary>
    internal Type? GroupKeyType { get; set; }

    /// <summary>The parameter the GroupBy key selector was written against.</summary>
    internal System.Linq.Expressions.ParameterExpression? GroupSourceParameter { get; set; }

    /// <summary>The type <see cref="ClientSelector"/> expects, when there is one.</summary>
    internal Type? ClientSelectorInput { get; set; }

    /// <summary>True once a Select has been pushed down.</summary>
    internal bool HasProjection => Projections.Count > 0;

    /// <summary>Renders the model as a statement the engine can run.</summary>
    internal SelectStatement ToStatement()
    {
        var projections = Projections;

        // An aggregate that the engine can compute becomes the whole projection list; the rows
        // themselves are never sent back.
        if (Shape is CuteResultShape.Count or CuteResultShape.LongCount)
        {
            projections = [new CuteProjection(new FunctionExpression("COUNT", [new StarExpression()]), "value")];
        }
        else if (Shape is CuteResultShape.Sum or CuteResultShape.Average or CuteResultShape.Min or CuteResultShape.Max)
        {
            var function = Shape switch
            {
                CuteResultShape.Sum => "SUM",
                CuteResultShape.Average => "AVG",
                CuteResultShape.Min => "MIN",
                _ => "MAX",
            };

            var argument = Projections.Count == 1
                ? Projections[0].Expression
                : throw new CuteTranslationException(
                    $"{function} needs exactly one value to aggregate. Add a Select before it.");

            projections = [new CuteProjection(new FunctionExpression(function, [argument]), "value")];
        }

        var limit = Limit;

        // Only the first row is ever needed for these, so the engine is told so rather than
        // materialising a collection the caller will discard.
        if (Shape is CuteResultShape.First or CuteResultShape.FirstOrDefault or CuteResultShape.Any)
        {
            limit = 1;
        }
        else if (Shape is CuteResultShape.Single or CuteResultShape.SingleOrDefault)
        {
            // Two, so "more than one" can still be detected and reported.
            limit = 2;
        }

        return new SelectStatement
        {
            Text = string.Empty,
            Collection = Collection,
            Projections = projections,
            Where = Where,
            GroupBy = GroupBy,
            Having = Having,
            OrderBy = OrderBy,
            Limit = limit,
            Offset = Offset,
            Distinct = Distinct,
        };
    }

    /// <summary>The CuteQL this query runs as.</summary>
    internal string ToCuteQL(bool indented) => CuteQLWriter.Write(ToStatement(), indented);
}
