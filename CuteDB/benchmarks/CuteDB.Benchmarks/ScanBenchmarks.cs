using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using CuteDB.Native;
using CuteDB.Query;
using CuteDB.Retail;

namespace CuteDB.Benchmarks;

/// <summary>
/// How fast CuteDB filters a collection, and what the three ways of doing it cost.
/// </summary>
/// <remarks>
/// <para>
/// This is the benchmark that matters most, because filtering is what a database spends its time
/// on and because CuteDB offers three routes to the same answer: the managed evaluator walking
/// encoded documents, the Rust accelerator doing the same walk natively, and an index skipping
/// most of the documents entirely. Running all three over one dataset is the only honest way to
/// say what the accelerator is worth.
/// </para>
/// <para>
/// The dataset is the Nusantara Retail sample at a fixed seed, so a number here refers to the same
/// rows every time it is measured.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 6)]
public class ScanBenchmarks
{
    private CuteDatabase _database = null!;
    private CuteCollection _orders = null!;
    private CuteCollection _indexed = null!;

    /// <summary>Orders in the collection being scanned.</summary>
    [Params(250_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _database = CuteDatabase.CreateInMemory();

        var scale = RetailScale.Demo with
        {
            Orders = Rows,
            Customers = Rows / 10,
            Products = Math.Max(500, Rows / 100),
        };

        NusantaraRetail.Seed(_database, scale);
        _orders = _database.Collection("orders");

        // A second copy with no indexes, so the scan benchmarks measure scanning rather than
        // accidentally hitting the index the seed creates.
        _indexed = _orders;

        var unindexed = _database.Collection("orders_raw");
        unindexed.InsertMany(_orders.All().Select(d => d.DeepClone()));
        _orders = unindexed;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        CuteNative.Disabled = false;
        _database.Dispose();
    }

    // --- equality on a nested path ---------------------------------------------------------

    [Benchmark(Baseline = true, Description = "scan, managed: address.city = 'Bandung'")]
    public int ScanEqualityManaged() => Scan("address.city = 'Bandung'", native: false);

    [Benchmark(Description = "scan, native: address.city = 'Bandung'")]
    public int ScanEqualityNative() => Scan("address.city = 'Bandung'", native: true);

    [Benchmark(Description = "index seek: address.city = 'Bandung'")]
    public int SeekEquality() => _indexed.CountWhere("address.city = 'Bandung'");

    // --- a compound predicate ---------------------------------------------------------------

    [Benchmark(Description = "scan, managed: status AND total")]
    public int ScanCompoundManaged() => Scan("status = 'selesai' AND total > 500000", native: false);

    [Benchmark(Description = "scan, native: status AND total")]
    public int ScanCompoundNative() => Scan("status = 'selesai' AND total > 500000", native: true);

    // --- text matching -----------------------------------------------------------------------

    [Benchmark(Description = "scan, managed: code LIKE 'SO-2025%'")]
    public int ScanLikeManaged() => Scan("code LIKE 'SO-2025%'", native: false);

    [Benchmark(Description = "scan, native: code LIKE 'SO-2025%'")]
    public int ScanLikeNative() => Scan("code LIKE 'SO-2025%'", native: true);

    // --- a field holding an array -----------------------------------------------------------

    [Benchmark(Description = "scan, managed: tags = 'promo'")]
    public int ScanArrayManaged() => Scan("customer.tier = 'platinum'", native: false);

    [Benchmark(Description = "scan, native: tags = 'promo'")]
    public int ScanArrayNative() => Scan("customer.tier = 'platinum'", native: true);

    private int Scan(string filter, bool native)
    {
        CuteNative.Disabled = !native;
        try
        {
            return _orders.CountWhere(filter);
        }
        finally
        {
            CuteNative.Disabled = false;
        }
    }
}

/// <summary>
/// Point lookups, aggregation and paging — the read operations that are not a filtering scan.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 6)]
public class ReadBenchmarks
{
    private CuteDatabase _database = null!;
    private CuteCollection _orders = null!;
    private CuteId[] _ids = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _database = CuteDatabase.CreateInMemory();
        NusantaraRetail.Seed(_database, RetailScale.Demo with { Orders = 250_000, Customers = 25_000 });

        _orders = _database.Collection("orders");
        _ids = [.. _orders.Find("units > 0", limit: 4_096).Select(d => d.Id)];
    }

    [GlobalCleanup]
    public void Cleanup() => _database.Dispose();

    [Benchmark(Description = "point lookup by id")]
    public CuteDocument? PointLookup() => _orders.FindById(_ids[_cursor++ % _ids.Length]);

    [Benchmark(Description = "GROUP BY city with two aggregates")]
    public int Aggregate() => _database.Execute(
        "SELECT address.city AS city, COUNT(*) AS n, SUM(total) AS revenue FROM orders GROUP BY address.city")
        .Rows.Count;

    [Benchmark(Description = "ORDER BY total DESC LIMIT 50")]
    public int TopN() => _database.Execute(
        "SELECT code, total FROM orders ORDER BY total DESC LIMIT 50").Rows.Count;

    [Benchmark(Description = "paged read, LIMIT 100 OFFSET 10000")]
    public int Page() => _database.Execute(
        "SELECT * FROM orders LIMIT 100 OFFSET 10000").Rows.Count;

    [Benchmark(Description = "parse a CuteQL statement")]
    public CuteStatement Parse() => CuteParser.ParseStatement(
        "SELECT address.city AS city, SUM(total) AS revenue FROM orders " +
        "WHERE status != 'dibatalkan' AND total BETWEEN 100000 AND 900000 " +
        "GROUP BY address.city HAVING COUNT(*) > 10 ORDER BY revenue DESC LIMIT 20");
}
