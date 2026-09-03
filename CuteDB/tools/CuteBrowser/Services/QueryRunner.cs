using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CuteDB.Linq;
using CuteDB.Query;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace CuteDB.Browser.Services;

/// <summary>Which language a query tab is written in.</summary>
public enum QueryLanguage
{
    /// <summary>CuteQL, run straight by the engine.</summary>
    CuteQL,

    /// <summary>C# LINQ, compiled and run as a script.</summary>
    Linq,
}

/// <summary>
/// What running a query produced.
/// </summary>
/// <param name="Columns">Column names, in order.</param>
/// <param name="Rows">The rows, each a map from column name to rendered value.</param>
/// <param name="RowCount">How many rows came back.</param>
/// <param name="AffectedCount">How many documents a write touched.</param>
/// <param name="Elapsed">Wall time, including compilation for a LINQ tab.</param>
/// <param name="EngineTime">What the engine itself reported.</param>
/// <param name="Plan">How the engine found the rows.</param>
/// <param name="GeneratedCuteQL">For a LINQ tab, the CuteQL it translated to.</param>
/// <param name="Message">A line for the status bar.</param>
/// <param name="Error">What went wrong, or null.</param>
public sealed record QueryOutcome(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    int RowCount,
    int AffectedCount,
    TimeSpan Elapsed,
    TimeSpan EngineTime,
    CuteQueryPlan? Plan,
    string? GeneratedCuteQL,
    string Message,
    string? Error)
{
    /// <summary>Whether the query ran.</summary>
    public bool Succeeded => Error is null;

    /// <summary>A failure, shaped so the caller does not have to fill in eleven fields.</summary>
    public static QueryOutcome Failed(string error, TimeSpan elapsed)
        => new([], [], 0, 0, elapsed, TimeSpan.Zero, null, null, "Failed", error);
}

/// <summary>
/// Runs what is in a query tab.
/// </summary>
/// <remarks>
/// <para>
/// Two languages, two very different mechanisms. CuteQL goes straight to
/// <see cref="CuteDatabase.Execute(string, CuteParameters?)"/>. LINQ is C#, so it is compiled by
/// Roslyn and run as a script with the database in scope — there is no way to evaluate a C#
/// expression tree that has not been compiled, and pretending otherwise would mean inventing a
/// second, worse language that merely looked like LINQ.
/// </para>
/// <para>
/// A script may declare its own types, which is what makes the LINQ tab usable against a schemaless
/// store: you write the POCO that describes the shape you care about, and
/// <c>db.Collection("orders").Query&lt;Order&gt;()</c> maps onto it. When the script returns an
/// <see cref="IQueryable"/>, its translated CuteQL is captured before enumeration, so the tab can
/// show what the engine was actually asked — which is the whole point of the feature.
/// </para>
/// <para>
/// Scripts run in this process with full trust. That is the same trust a query tab already has —
/// <c>DELETE FROM orders</c> is not less destructive for being short — but it is worth being
/// explicit about, and it is why the log records every run.
/// </para>
/// </remarks>
public sealed class QueryRunner(Workspace workspace)
{
    private static readonly string[] ScriptImports =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "CuteDB",
        "CuteDB.Linq",
        "CuteDB.Query",
        "CuteDB.Mapping",
    ];

    private ScriptOptions? _scriptOptions;

    /// <summary>Runs a tab's text and shapes the result for the grid.</summary>
    public async Task<QueryOutcome> RunAsync(string text, QueryLanguage language, int maxRows, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return QueryOutcome.Failed("There is nothing to run.", TimeSpan.Zero);
        }

        var clock = Stopwatch.StartNew();

        try
        {
            return language == QueryLanguage.CuteQL
                ? await Task.Run(() => RunCuteQL(text, maxRows, clock), token)
                : await RunLinqAsync(text, maxRows, clock, token);
        }
        catch (OperationCanceledException)
        {
            return QueryOutcome.Failed("Cancelled.", clock.Elapsed);
        }
        catch (Exception exception)
        {
            return QueryOutcome.Failed(Explain(exception), clock.Elapsed);
        }
    }

    /// <summary>
    /// Parses a query without running it, for the Check button.
    /// </summary>
    /// <remarks>
    /// For CuteQL this is a real parse plus, where the statement is a SELECT, a plan — so Check
    /// tells you not only that it is valid but how it would be answered. For LINQ it is a Roslyn
    /// compile with no execution, which catches everything except what only happens at runtime.
    /// </remarks>
    public async Task<(bool Ok, string Message)> CheckAsync(string text, QueryLanguage language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, "There is nothing to check.");
        }

        try
        {
            if (language == QueryLanguage.CuteQL)
            {
                var statements = SplitStatements(text);
                foreach (var statement in statements)
                {
                    CuteParser.ParseStatement(statement);
                }

                var plan = workspace.IsOpen && statements.Count == 1
                    ? TryExplain(statements[0])
                    : null;

                return (true, plan is null
                    ? $"{statements.Count} statement{(statements.Count == 1 ? string.Empty : "s")} parsed."
                    : $"Parsed. {plan}");
            }

            var script = CSharpScript.Create<object>(text, await OptionsAsync(), typeof(ScriptContext));
            var diagnostics = script.Compile();
            var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();

            return errors.Count == 0
                ? (true, "Compiles.")
                : (false, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        }
        catch (Exception exception)
        {
            return (false, Explain(exception));
        }
    }

    /// <summary>Splits a tab into statements on semicolons outside string literals.</summary>
    /// <remarks>
    /// A tab holding several statements is normal — seed data, then the query that reads it — so
    /// the split has to survive a semicolon inside a quoted value, which a plain <c>Split(';')</c>
    /// would not.
    /// </remarks>
    public static List<string> SplitStatements(string text)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'')
            {
                // '' inside a string is an escaped quote, not the end of one.
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                    continue;
                }

                inString = !inString;
            }

            if (c == ';' && !inString)
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }

            current.Clear();
        }
    }

    private QueryOutcome RunCuteQL(string text, int maxRows, Stopwatch clock)
    {
        var database = workspace.Require();
        var statements = SplitStatements(text);

        if (statements.Count == 0)
        {
            return QueryOutcome.Failed("There is nothing to run.", clock.Elapsed);
        }

        CuteQueryResult? last = null;
        var affected = 0;
        var engineTime = TimeSpan.Zero;

        // Every statement runs; the grid shows the last one that returned rows, which is what
        // "seed, then select" wants and what every SQL tool does.
        foreach (var statement in statements)
        {
            last = database.Execute(statement);
            affected += last.AffectedCount;
            engineTime += last.Duration;

            workspace.Log.Query("query", $"{Collapse(statement)} — {last}");
        }

        var result = last!;
        var (columns, rows) = Shape(result, maxRows);

        if (result.Kind != CuteQueryKind.Select)
        {
            workspace.NotifySchemaChanged();
        }

        var message = result.Kind == CuteQueryKind.Select
            ? $"{result.Rows.Count:N0} rows in {engineTime.TotalMilliseconds:N2} ms"
            : $"{affected:N0} affected in {engineTime.TotalMilliseconds:N2} ms";

        return new QueryOutcome(
            columns,
            rows,
            result.Rows.Count,
            affected,
            clock.Elapsed,
            engineTime,
            result.Plan,
            GeneratedCuteQL: null,
            message,
            Error: null);
    }

    private async Task<QueryOutcome> RunLinqAsync(string text, int maxRows, Stopwatch clock, CancellationToken token)
    {
        var database = workspace.Require();
        var context = new ScriptContext(database);

        var value = await CSharpScript.EvaluateAsync<object?>(text, await OptionsAsync(), context, cancellationToken: token);

        // The translated CuteQL is read before enumerating, because enumerating a queryable is
        // what turns it into rows and there is nothing left to ask afterwards.
        string? generated = null;
        if (value is IQueryable queryable)
        {
            generated = TryRenderCuteQL(queryable);
        }

        var (columns, rows, count) = Flatten(value, maxRows);

        workspace.Log.Query(
            "linq",
            generated is null
                ? $"C# script returned {count:N0} rows in {clock.Elapsed.TotalMilliseconds:N2} ms"
                : $"{generated} — {count:N0} rows in {clock.Elapsed.TotalMilliseconds:N2} ms");

        return new QueryOutcome(
            columns,
            rows,
            count,
            AffectedCount: 0,
            clock.Elapsed,
            EngineTime: TimeSpan.Zero,
            Plan: null,
            generated,
            $"{count:N0} rows in {clock.Elapsed.TotalMilliseconds:N2} ms",
            Error: null);
    }

    /// <summary>
    /// Asks a queryable for its CuteQL, without caring what element type it has.
    /// </summary>
    /// <remarks>
    /// <c>ToCuteQL</c> is generic in the element type and a script's element type is usually
    /// anonymous, so the call is made through reflection rather than by naming the type. A failure
    /// here is not an error: the script may have returned a queryable that is not CuteDB's.
    /// </remarks>
    private static string? TryRenderCuteQL(IQueryable queryable)
    {
        try
        {
            if (queryable.Provider is not CuteQueryProvider)
            {
                return null;
            }

            var method = typeof(CuteQueryableExtensions)
                .GetMethod(nameof(CuteQueryableExtensions.ToCuteQL), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(queryable.ElementType);

            return (string?)method.Invoke(null, [queryable, false]);
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            return null;
        }
    }

    private CuteQueryPlan? TryExplain(string statement)
    {
        try
        {
            return workspace.Require().Explain(statement);
        }
        catch (Exception)
        {
            // Explain only answers for a SELECT; anything else is not a failure of the check.
            return null;
        }
    }

    private async Task<ScriptOptions> OptionsAsync()
    {
        if (_scriptOptions is not null)
        {
            return _scriptOptions;
        }

        return _scriptOptions = await Task.Run(() => ScriptOptions.Default
            .WithImports(ScriptImports)
            .WithReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(IQueryable).Assembly,
                typeof(CuteDatabase).Assembly));
    }

    // ---- shaping results for the grid ------------------------------------------------------

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)
        Shape(CuteQueryResult result, int maxRows)
    {
        var columns = result.Columns.Count > 0
            ? result.Columns.ToList()
            : ColumnsOf(result.Rows);

        var rows = new List<IReadOnlyDictionary<string, string>>(Math.Min(result.Rows.Count, maxRows));

        foreach (var row in result.Rows.Take(maxRows))
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                cells[column] = Render(row[column]);
            }

            rows.Add(cells);
        }

        return (columns, rows);
    }

    /// <summary>The union of the keys across a set of documents, in first-seen order.</summary>
    /// <remarks>
    /// A schemaless store means <c>SELECT *</c> over two documents can produce two different sets
    /// of fields. Taking the union rather than the first document's keys is the difference between
    /// a grid that shows the data and one that silently drops a column.
    /// </remarks>
    private static List<string> ColumnsOf(IReadOnlyList<CuteObject> rows)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(key);
                }
            }
        }

        return columns;
    }

    private static string Render(CuteValue value) => value.Type switch
    {
        CuteType.Missing => string.Empty,
        CuteType.Null => "null",
        CuteType.Object or CuteType.Array => value.ToJson(indented: false),
        CuteType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        CuteType.Decimal => value.AsDecimal.ToString(CultureInfo.InvariantCulture),
        _ => value.ToDisplayString(),
    };

    /// <summary>
    /// Turns whatever a script returned into columns and rows.
    /// </summary>
    /// <remarks>
    /// A script may return a sequence of documents, of POCOs, of anonymous types, or a single
    /// scalar from an aggregate. All four are worth showing, so all four are handled: a scalar
    /// becomes one row of one column called <c>value</c>, and objects are read by their public
    /// properties in declaration order.
    /// </remarks>
    private static (IReadOnlyList<string>, IReadOnlyList<IReadOnlyDictionary<string, string>>, int)
        Flatten(object? value, int maxRows)
    {
        if (value is null)
        {
            return (["value"], [Single("value", "null")], 0);
        }

        if (value is not System.Collections.IEnumerable sequence || value is string)
        {
            return (["value"], [Single("value", Text(value))], 1);
        }

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var count = 0;

        foreach (var item in sequence)
        {
            count++;
            if (rows.Count >= maxRows)
            {
                continue;
            }

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);

            switch (item)
            {
                case CuteDocument document:
                    Read(document.Root, cells);
                    break;

                case CuteObject document:
                    Read(document, cells);
                    break;

                case null:
                    cells["value"] = "null";
                    break;

                default:
                {
                    var type = item.GetType();
                    if (IsScalar(type))
                    {
                        cells["value"] = Text(item);
                    }
                    else
                    {
                        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (property.GetIndexParameters().Length == 0)
                            {
                                cells[property.Name] = Text(property.GetValue(item));
                            }
                        }
                    }

                    break;
                }
            }

            foreach (var key in cells.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(key);
                }
            }

            rows.Add(cells);
        }

        if (columns.Count == 0)
        {
            columns.Add("value");
        }

        return (columns, rows, count);

        static void Read(CuteObject document, Dictionary<string, string> cells)
        {
            foreach (var (key, field) in document)
            {
                cells[key] = Render(field);
            }
        }
    }

    private static Dictionary<string, string> Single(string column, string value)
        => new(StringComparer.Ordinal) { [column] = value };

    private static bool IsScalar(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(Guid)
            || type == typeof(CuteId);

    private static string Text(object? value) => value switch
    {
        null => "null",
        CuteValue cute => Render(cute),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Collapse(string text)
    {
        var single = string.Join(' ', text.Split('\n', '\r', '\t').Select(part => part.Trim()).Where(part => part.Length > 0));
        return single.Length <= 160 ? single : single[..157] + "…";
    }

    /// <summary>
    /// Turns an exception into something a person can act on.
    /// </summary>
    /// <remarks>
    /// Roslyn wraps a script's own exception in a <see cref="TargetInvocationException"/> and a
    /// <see cref="AggregateException"/> can hide the one line that matters several levels down.
    /// Unwrapping is the difference between "one or more errors occurred" and the actual message.
    /// </remarks>
    private static string Explain(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    exception = aggregate.InnerExceptions[0];
                    continue;

                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    continue;

                case CompilationErrorException compilation:
                    return string.Join(Environment.NewLine, compilation.Diagnostics);

                default:
                    return exception.Message;
            }
        }
    }
}

/// <summary>
/// What a LINQ tab's script can see.
/// </summary>
/// <remarks>
/// Deliberately small. <c>db</c> is the open database and <c>Q</c> is a shorthand for a typed
/// queryable, which together cover everything the LINQ provider offers. A bigger surface would be
/// a second API to document and keep correct, and the point of the tab is to be ordinary C#.
/// </remarks>
public sealed class ScriptContext(CuteDatabase database)
{
    /// <summary>The open database.</summary>
    public CuteDatabase db { get; } = database;

    /// <summary>A typed queryable over a collection: <c>Q&lt;Order&gt;("orders")</c>.</summary>
    public IQueryable<T> Q<T>(string collection) => db.Collection(collection).Query<T>();

    /// <summary>Runs a CuteQL statement from inside a script.</summary>
    public CuteQueryResult Sql(string statement) => db.Execute(statement);
}
