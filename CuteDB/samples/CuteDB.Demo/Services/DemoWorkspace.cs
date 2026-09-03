using System.Diagnostics;
using CuteDB.Native;
using CuteDB.Query;
using CuteDB.Retail;

namespace CuteDB.Demo.Services;

/// <summary>One line printed on the till roll: what the engine did, and what it cost.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Label">A short description of the operation.</param>
/// <param name="Strategy">The access method — scan, index seek, or a write.</param>
/// <param name="Examined">Rows the access method looked at.</param>
/// <param name="Matched">Rows that survived the predicate.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Native">Whether the Rust accelerator ran the scan.</param>
public readonly record struct TapeEntry(
    DateTime At,
    string Label,
    string Strategy,
    int Examined,
    int Matched,
    TimeSpan Duration,
    bool Native)
{
    /// <summary>The timing, scaled so the number stays readable.</summary>
    public string DurationText => Duration.TotalMilliseconds switch
    {
        < 1 => $"{Duration.TotalMicroseconds:N0} µs",
        < 1_000 => $"{Duration.TotalMilliseconds:N2} ms",
        _ => $"{Duration.TotalSeconds:N2} s",
    };
}

/// <summary>
/// The one open database every view shares, plus the till-roll history.
/// </summary>
/// <remarks>
/// <para>
/// The demo runs entirely in memory. That is not a shortcut around persistence — the storage tests
/// cover that thoroughly — it is so the app starts clean every time and so someone poking at the
/// CRUD and bulk sections cannot leave a half-written file behind between runs.
/// </para>
/// <para>
/// Every query the interface runs goes through <see cref="Run"/>, which records what the engine
/// did on the tape. That is the point of the app: a result grid is table stakes, but showing
/// whether the same answer arrived via a scan, an accelerated scan or an index seek is the thing
/// worth demonstrating.
/// </para>
/// </remarks>
public sealed class DemoWorkspace : IDisposable
{
    private const int MaxTapeEntries = 40;

    private readonly List<TapeEntry> _tape = [];

    /// <summary>The open database.</summary>
    public CuteDatabase Database { get; private set; } = null!;

    /// <summary>The order history — the collection most of the demos work against.</summary>
    public CuteCollection Orders => Database.Collection("orders");

    /// <summary>The product catalogue.</summary>
    public CuteCollection Products => Database.Collection("products");

    /// <summary>The customer directory.</summary>
    public CuteCollection Customers => Database.Collection("customers");

    /// <summary>The outlets.</summary>
    public CuteCollection Stores => Database.Collection("stores");

    /// <summary>The till roll, newest first.</summary>
    public IReadOnlyList<TapeEntry> Tape => _tape;

    /// <summary>How long the initial load took.</summary>
    public TimeSpan LoadDuration { get; private set; }

    /// <summary>Raised when a new entry is printed on the tape.</summary>
    public event Action<TapeEntry>? TapePrinted;

    /// <summary>Raised when a collection's contents change, so views can refresh.</summary>
    public event Action? DataChanged;

    /// <summary>Builds the sample dataset.</summary>
    public void Load(RetailScale? scale = null)
    {
        var timer = Stopwatch.StartNew();

        Database = CuteDatabase.CreateInMemory();
        NusantaraRetail.Seed(Database, scale ?? RetailScale.Demo);

        timer.Stop();
        LoadDuration = timer.Elapsed;
    }

    /// <summary>
    /// Runs a CuteQL statement and prints what the engine did on the tape.
    /// </summary>
    public CuteQueryResult Run(string query, string label, CuteParameters? parameters = null)
    {
        var result = Database.Execute(query, parameters);

        Print(new TapeEntry(
            DateTime.Now,
            label,
            result.Kind == CuteQueryKind.Select ? result.Plan.Strategy : Describe(result.Kind),
            result.Plan.CandidateRows,
            result.Kind == CuteQueryKind.Select ? result.Plan.MatchedRows : result.AffectedCount,
            result.Duration,
            result.Plan.UsedNativeScanner));

        if (result.Kind != CuteQueryKind.Select)
        {
            DataChanged?.Invoke();
        }

        return result;
    }

    /// <summary>Times an arbitrary operation and prints it on the tape.</summary>
    public T Measure<T>(string label, string strategy, Func<T> operation, int examined = 0)
    {
        var timer = Stopwatch.StartNew();
        var value = operation();
        timer.Stop();

        Print(new TapeEntry(DateTime.Now, label, strategy, examined, 0, timer.Elapsed, false));
        return value;
    }

    /// <summary>Prints one entry on the tape.</summary>
    public void Print(TapeEntry entry)
    {
        _tape.Insert(0, entry);

        // The tape is a rolling window, not a log: a session that runs a thousand queries should
        // not grow without bound, and nobody scrolls back forty entries.
        if (_tape.Count > MaxTapeEntries)
        {
            _tape.RemoveAt(_tape.Count - 1);
        }

        TapePrinted?.Invoke(entry);
    }

    /// <summary>Announces that a collection changed, so open views can refresh.</summary>
    public void NotifyDataChanged() => DataChanged?.Invoke();

    /// <summary>A one-line description of the engine, for the header band.</summary>
    public string EngineLine
    {
        get
        {
            var scanner = CuteNative.IsAvailable
                ? $"pemindai native · {CuteNative.Version}"
                : "pemindai terkelola / managed scanner";

            return $"cutedb {Version} · {scanner}";
        }
    }

    /// <summary>The engine version.</summary>
    public static string Version
        => typeof(CuteDatabase).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    /// <summary>Total documents across every collection.</summary>
    public int DocumentCount => Database.Stats().DocumentCount;

    /// <inheritdoc />
    public void Dispose() => Database?.Dispose();

    private static string Describe(CuteQueryKind kind) => kind switch
    {
        CuteQueryKind.Insert => "insert",
        CuteQueryKind.Update => "update",
        CuteQueryKind.Delete => "delete",
        _ => "query",
    };
}
