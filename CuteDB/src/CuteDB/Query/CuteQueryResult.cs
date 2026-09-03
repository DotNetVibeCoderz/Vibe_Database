using System.Diagnostics;

namespace CuteDB.Query;

/// <summary>What kind of statement produced a result.</summary>
public enum CuteQueryKind
{
    /// <summary>A <c>SELECT</c>: rows come back.</summary>
    Select,

    /// <summary>An <c>INSERT</c>: <see cref="CuteQueryResult.AffectedCount"/> documents were added.</summary>
    Insert,

    /// <summary>An <c>UPDATE</c>: documents were modified.</summary>
    Update,

    /// <summary>A <c>DELETE</c>: documents were removed.</summary>
    Delete,
}

/// <summary>
/// What a CuteQL statement produced: rows for a <c>SELECT</c>, a count for everything else, plus
/// how the engine went about it.
/// </summary>
/// <remarks>
/// <see cref="Columns"/> is worth a word. A document store has no schema, so the columns of a
/// <c>SELECT *</c> are discovered from the rows that came back rather than declared in advance:
/// the list is the union of every field name present, in first-seen order. That is what lets the
/// CLI and the demo render a table without asking the caller what shape to expect, and why a
/// result over ragged documents has columns that some rows leave empty.
/// </remarks>
public sealed class CuteQueryResult
{
    internal CuteQueryResult(
        CuteQueryKind kind,
        IReadOnlyList<string> columns,
        IReadOnlyList<CuteObject> rows,
        int affectedCount,
        TimeSpan duration,
        CuteQueryPlan plan)
    {
        Kind = kind;
        Columns = columns;
        Rows = rows;
        AffectedCount = affectedCount;
        Duration = duration;
        Plan = plan;
    }

    /// <summary>Which sort of statement this came from.</summary>
    public CuteQueryKind Kind { get; }

    /// <summary>The field names present across the returned rows, in first-seen order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The rows. Empty for anything but a <c>SELECT</c>.</summary>
    public IReadOnlyList<CuteObject> Rows { get; }

    /// <summary>Documents inserted, updated or deleted. For a <c>SELECT</c>, the row count.</summary>
    public int AffectedCount { get; }

    /// <summary>How long the statement took, measured around execution only.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How the rows were found.</summary>
    public CuteQueryPlan Plan { get; }

    /// <summary>The rows as documents, for callers that want to keep working with them.</summary>
    public IEnumerable<CuteDocument> AsDocuments()
        => Rows.Select(static row => new CuteDocument(row, assignId: false));

    /// <summary>The single value of a one-row, one-column result — what an aggregate returns.</summary>
    public CuteValue Scalar()
        => Rows.Count > 0 && Columns.Count > 0 ? Rows[0][Columns[0]] : CuteValue.Missing;

    /// <summary>Renders the rows as a JSON array.</summary>
    public string ToJson(bool indented = true)
    {
        var array = new CuteArray(Rows.Count);
        foreach (var row in Rows)
        {
            array.Add(CuteValue.Object(row));
        }

        return CuteJson.Write(CuteValue.Array(array), indented);
    }

    /// <inheritdoc />
    public override string ToString() => Kind == CuteQueryKind.Select
        ? $"{Rows.Count} rows in {Duration.TotalMilliseconds:N2} ms — {Plan}"
        : $"{AffectedCount} documents {Kind.ToString().ToLowerInvariant()}d in {Duration.TotalMilliseconds:N2} ms";

    internal static CuteQueryResult ForWrite(CuteQueryKind kind, int affected, TimeSpan duration)
        => new(kind, [], [], affected, duration, default);

    /// <summary>Builds the column list from the rows, preserving first-seen order.</summary>
    internal static List<string> DeriveColumns(IReadOnlyList<CuteObject> rows)
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
}

/// <summary>Times a block and reports the elapsed span, without the allocation a Stopwatch costs.</summary>
internal readonly struct QueryTimer
{
    private readonly long _start;

    private QueryTimer(long start) => _start = start;

    internal static QueryTimer Start() => new(Stopwatch.GetTimestamp());

    internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
}
