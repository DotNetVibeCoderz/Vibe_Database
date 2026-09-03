using System.Linq.Expressions;
using CuteDB.Linq;
using CuteDB.Query;

namespace CuteDB;

/// <summary>
/// Inspecting and running a LINQ query.
/// </summary>
/// <remarks>
/// <see cref="ToCuteQL{T}(IQueryable{T}, bool)"/> is the important one. A query provider you cannot
/// see through is one you cannot debug: when a LINQ query returns the wrong rows, or is slower than
/// expected, the first question is always "what did that actually run as?" — and the answer here is
/// text you can paste straight into <c>cutedb shell</c>.
/// </remarks>
public static class CuteQueryableExtensions
{
    /// <summary>
    /// The CuteQL this query will run as. Does not execute it.
    /// </summary>
    /// <param name="source">Any CuteDB LINQ query.</param>
    /// <param name="indented">Puts each clause on its own line.</param>
    /// <example>
    /// <code>
    /// var query = orders.Query&lt;Order&gt;()
    ///     .Where(o =&gt; o.Address.City == "Bandung" &amp;&amp; o.Total &gt; 500_000m)
    ///     .OrderByDescending(o =&gt; o.Total)
    ///     .Take(10);
    ///
    /// Console.WriteLine(query.ToCuteQL());
    /// // SELECT * FROM orders WHERE address.city = 'Bandung' AND total > 500000
    /// //   ORDER BY total DESC LIMIT 10
    /// </code>
    /// </example>
    public static string ToCuteQL<T>(this IQueryable<T> source, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Provider(source).Translate(source.Expression).ToCuteQL(indented);
    }

    /// <summary>The parsed statement this query runs as, for tooling that wants the tree.</summary>
    public static SelectStatement ToCuteQLStatement<T>(this IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Provider(source).Translate(source.Expression).ToStatement();
    }

    /// <summary>
    /// How the engine will find the rows — index seek or scan, and whether the native scanner runs.
    /// </summary>
    /// <remarks>
    /// Runs the query's access path without materialising results, so it is cheap enough to call
    /// while tuning. The number to watch is candidates against matched: a scan that examines every
    /// document to return a handful is the one that wants an index.
    /// </remarks>
    public static CuteQueryPlan ExplainCuteQL<T>(this IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var provider = Provider(source);
        var model = provider.Translate(source.Expression);
        return provider.Collection.Database.Explain(CuteQLWriter.Write(model.ToStatement()));
    }

    /// <summary>
    /// Runs the query and returns the results together with the timing and plan.
    /// </summary>
    /// <example>
    /// <code>
    /// var (rows, diagnostics) = query.ToListWithDiagnostics();
    /// Console.WriteLine($"{diagnostics.CuteQL}");
    /// Console.WriteLine($"{diagnostics.Duration.TotalMilliseconds:N2} ms · {diagnostics.Plan}");
    /// </code>
    /// </example>
    public static (IReadOnlyList<T> Rows, CuteQueryDiagnostics Diagnostics) ToListWithDiagnostics<T>(this IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var provider = Provider(source);
        var model = provider.Translate(source.Expression);
        var statement = CuteQLWriter.Write(model.ToStatement());
        var result = provider.Collection.Database.Execute(statement);

        var rows = CuteQueryProvider.Materialize(model, result).Cast<T>().ToList();
        return (rows, new CuteQueryDiagnostics(statement, result.Duration, result.Plan, result.Rows.Count));
    }

    private static CuteQueryProvider Provider<T>(IQueryable<T> source)
        => source.Provider as CuteQueryProvider
            ?? throw new CuteDbException(
                "This is not a CuteDB query. These extensions work on the result of collection.Query<T>().");
}

/// <summary>What a LINQ query cost, and how it ran.</summary>
/// <param name="CuteQL">The statement the expression tree translated to.</param>
/// <param name="Duration">How long the engine took.</param>
/// <param name="Plan">The access path used.</param>
/// <param name="RowsReturned">Rows the engine handed back before materialisation.</param>
public readonly record struct CuteQueryDiagnostics(
    string CuteQL,
    TimeSpan Duration,
    CuteQueryPlan Plan,
    int RowsReturned)
{
    /// <summary>A one-line summary for a log or a status bar.</summary>
    public override string ToString()
        => $"{RowsReturned:N0} rows · {Duration.TotalMilliseconds:N2} ms · {Plan}";
}
