using System.Globalization;
using MemSharp.Query;

namespace MemSharp;

/// <summary>The result of a <c>SELECT</c>: named columns and the rows under them.</summary>
/// <param name="Columns">Column names, in projection order.</param>
/// <param name="Rows">Row values, each aligned to <paramref name="Columns"/>. A null cell is a SQL NULL.</param>
/// <param name="Affected">Rows removed, for a <c>DELETE</c>; 0 for a <c>SELECT</c>.</param>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, int Affected)
{
    /// <summary>Number of rows returned.</summary>
    public int Count => Rows.Count;

    /// <summary>An empty result with no columns.</summary>
    public static QueryResult Empty { get; } = new(Array.Empty<string>(), Array.Empty<string?[]>(), 0);
}

public sealed partial class MemDb
{
    /// <summary>
    /// Runs a query against the keyspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one table is <c>KEYS</c>: one row per live key, with columns <c>key</c>, <c>type</c>,
    /// <c>size</c>, <c>ttl</c> and <c>value</c>. See <see cref="SqlParser"/> for the grammar.
    /// </para>
    /// <para>
    /// When the filter contains a top-level <c>key LIKE</c> or <c>key =</c>, that pattern is pushed
    /// into the scan, so the query touches only matching keys instead of walking the whole
    /// keyspace. Everything else is evaluated per row.
    /// </para>
    /// <example>
    /// <code>
    /// db.ExecuteSql("SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 4 ORDER BY size DESC LIMIT 10");
    /// db.ExecuteSql("DELETE FROM keys WHERE type = 'String' AND ttl &lt; 60");
    /// </code>
    /// </example>
    /// </remarks>
    /// <exception cref="MemSharpCommandException">The query could not be parsed.</exception>
    public QueryResult ExecuteSql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Execute(SqlParser.Parse(sql));
    }

    /// <summary>Runs an already-parsed query - reuse the plan when running the same query repeatedly.</summary>
    public QueryResult Execute(SqlQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = _clock.GetUtcNow();
        var matches = new List<KeyInfo>();

        // A pushed-down key pattern turns a full keyspace walk into a targeted one. The predicate
        // still runs over the survivors, because the pattern is necessary but not sufficient.
        if (query.KeyPattern is { } pattern)
        {
            foreach (var key in Scan(pattern))
            {
                if (Describe(key) is not { } info) continue;
                if (query.Where is null || query.Where.Matches(info, now)) matches.Add(info);
            }
        }
        else
        {
            foreach (var info in Query())
            {
                if (query.Where is null || query.Where.Matches(info, now)) matches.Add(info);
            }
        }

        if (query.IsDelete)
        {
            int removed = 0;
            foreach (var info in matches)
            {
                if (Delete(info.Key)) removed++;
            }
            return new QueryResult(Array.Empty<string>(), Array.Empty<string?[]>(), removed);
        }

        if (query.OrderBy is { } sortColumn)
        {
            matches.Sort((a, b) => CompareBy(sortColumn, a, b, now) * (query.Descending ? -1 : 1));
        }

        IEnumerable<KeyInfo> page = matches;
        if (query.Offset > 0) page = page.Skip(query.Offset);
        if (query.Limit >= 0) page = page.Take(query.Limit);

        var columns = query.Columns.Count > 0
            ? query.Columns
            : new[] { QueryColumn.Key, QueryColumn.Type, QueryColumn.Size, QueryColumn.Ttl, QueryColumn.Value };

        var names = new string[columns.Count];
        for (int i = 0; i < columns.Count; i++) names[i] = columns[i].ToString().ToLowerInvariant();

        var rows = new List<string?[]>();
        foreach (var info in page)
        {
            var row = new string?[columns.Count];
            for (int i = 0; i < columns.Count; i++) row[i] = Render(columns[i], info, now);
            rows.Add(row);
        }

        return new QueryResult(names, rows, 0);
    }

    private static int CompareBy(QueryColumn column, in KeyInfo a, in KeyInfo b, DateTimeOffset now) => column switch
    {
        QueryColumn.Key => string.CompareOrdinal(a.Key, b.Key),
        QueryColumn.Type => a.Type.CompareTo(b.Type),
        QueryColumn.Size => a.Size.CompareTo(b.Size),

        // A key with no TTL sorts after every key that has one: "never expires" is the largest
        // remaining lifetime there is, and sorting it as 0 would put permanent keys first under
        // `ORDER BY ttl`, which reads as the opposite of what it means.
        QueryColumn.Ttl => (TtlSeconds(a, now) ?? double.MaxValue).CompareTo(TtlSeconds(b, now) ?? double.MaxValue),

        QueryColumn.Value => string.CompareOrdinal(a.StringValue, b.StringValue),
        _ => 0,
    };

    private static double? TtlSeconds(in KeyInfo key, DateTimeOffset now) =>
        key.ExpiresAt is { } expiry ? (expiry - now).TotalSeconds : null;

    private static string? Render(QueryColumn column, in KeyInfo key, DateTimeOffset now) => column switch
    {
        QueryColumn.Key => key.Key,
        QueryColumn.Type => key.Type.ToString(),
        QueryColumn.Size => key.Size.ToString(CultureInfo.InvariantCulture),
        QueryColumn.Ttl => key.ExpiresAt is { } expiry
            ? (expiry - now).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            : null,
        QueryColumn.Value => key.StringValue,
        _ => null,
    };
}
