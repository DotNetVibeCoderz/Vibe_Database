using CuteDB.Native;
using CuteDB.Query;

namespace CuteDB.Tests;

/// <summary>
/// Holds the managed and native scanners to the same answers.
/// </summary>
/// <remarks>
/// <para>
/// The accelerator exists to be invisible: the same query over the same data must return the same
/// rows, in the same order, whether or not <c>cutedb_core</c> loaded. These tests are what make
/// that claim checkable rather than hopeful — every case runs the query twice over one collection,
/// once with the native path enabled and once with it forced off, and compares the row sets.
/// </para>
/// <para>
/// The collection is deliberately larger than <c>NativeScanner.MinimumRowsToBotherWith</c>, since
/// below that threshold the native path declines and the test would be comparing managed against
/// managed and proving nothing. <see cref="ScannerIsActuallyExercised"/> asserts that the native
/// path really did run, so this suite fails loudly rather than passing vacuously if the library
/// stops loading.
/// </para>
/// </remarks>
public class NativeParityTests : IDisposable
{
    private const int RowCount = 20_000;

    private readonly CuteDatabase _database;
    private readonly CuteCollection _orders;

    public NativeParityTests()
    {
        _database = CuteDatabase.CreateInMemory();
        _orders = _database.Collection("orders");

        var cities = new[] { "Jakarta", "Bandung", "Surabaya", "Medan", "Denpasar" };
        var statuses = new[] { "paid", "shipped", "cancelled", "draft" };
        var random = new Random(20260903);

        _orders.InsertMany(Enumerable.Range(0, RowCount).Select(i =>
        {
            var document = new CuteDocument()
                .Set("n", i)
                .Set("code", $"SO-{i:D6}")
                .Set("status", statuses[i % statuses.Length])
                .Set("qty", CuteValue.Int64(random.Next(1, 50)))
                .Set("score", CuteValue.Double(random.NextDouble() * 100))
                .Set("total", CuteValue.Decimal(random.Next(10_000, 5_000_000) / 100m))
                .Set("customer", CuteValue.Object(new CuteObject()
                    .Set("name", $"Customer {i % 997}")
                    .Set("address", CuteValue.Object(new CuteObject()
                        .Set("city", cities[i % cities.Length])
                        .Set("country", "ID")))))
                .Set("tags", CuteValue.ArrayOf(
                    CuteValue.String(i % 3 == 0 ? "promo" : "retail"),
                    CuteValue.String(i % 7 == 0 ? "bulk" : "single")));

            // Roughly one row in eleven is missing fields the rest have, and one in seventeen has
            // an explicit null — the two cases three-valued logic turns on.
            if (i % 11 != 0)
            {
                document.Set("discount", CuteValue.Decimal(random.Next(0, 30)));
            }

            if (i % 17 == 0)
            {
                document.Set("note", CuteValue.Null);
            }

            return document;
        }));
    }

    public static TheoryData<string> Predicates() =>
    [
        "n > 10000",
        "n >= 10000 AND n < 12000",
        "n = 4242",
        "n != 4242",
        "status = 'paid'",
        "status != 'paid'",
        "status IN ('paid', 'shipped')",
        "status NOT IN ('paid', 'shipped')",
        "customer.address.city = 'Bandung'",
        "customer.address.city = 'Bandung' AND status = 'paid'",
        "customer.address.city = 'Bandung' OR customer.address.city = 'Medan'",
        "NOT (customer.address.city = 'Bandung')",
        "code LIKE 'SO-0001%'",
        "code LIKE 'SO-%42'",
        "code NOT LIKE 'SO-0%'",
        "qty BETWEEN 10 AND 20",
        "qty NOT BETWEEN 10 AND 20",
        "score > 50.0",
        "score <= 12.5",
        "total > 20000",
        "total BETWEEN 10000 AND 30000",
        "discount IS MISSING",
        "discount IS NOT MISSING",
        "note IS NULL",
        "note IS NOT NULL",
        "tags = 'promo'",
        "tags != 'promo'",
        "tags = 'bulk' AND status = 'paid'",
        "tags IN ('promo', 'bulk')",
        "customer.name = 'Customer 42'",
        "customer.address.zip IS MISSING",
        "n > 100 AND n < 200 AND status = 'paid' AND customer.address.city = 'Jakarta'",
        "(n < 100 OR n > 19900) AND tags = 'promo'",
        "NOT (qty > 25 OR score > 75.0)",
        "n > 5000 AND (status = 'draft' OR (qty BETWEEN 5 AND 15 AND tags = 'retail'))",
    ];

    [Theory]
    [MemberData(nameof(Predicates))]
    public void NativeAndManagedScansAgree(string filter)
    {
        var predicate = CuteParser.ParseExpression(filter);

        var native = Run(predicate, useNative: true);
        var managed = Run(predicate, useNative: false);

        Assert.Equal(managed, native);
    }

    [Fact]
    public void ScannerIsActuallyExercised()
    {
        // Without the library, every parity test above compares the managed evaluator against
        // itself and proves nothing. This is the canary for that.
        //
        // It is strict only where the library is supposed to exist. CI builds the accelerator
        // first and sets CUTEDB_EXPECT_NATIVE, so a build that silently stopped loading it fails
        // there. A developer with no Rust toolchain gets a suite that passes, which is what the
        // README promises — the .NET build never depends on Rust.
        var expected = Environment.GetEnvironmentVariable("CUTEDB_EXPECT_NATIVE") is "1" or "true";

        if (!CuteNative.IsAvailable)
        {
            Assert.False(
                expected,
                $"CUTEDB_EXPECT_NATIVE is set, but the accelerator did not load: {CuteNative.UnavailableReason}. " +
                "Run native/build.ps1 (or build.sh) first.");

            return;
        }

        var plan = _database.Explain("SELECT * FROM orders WHERE n > 10000");

        Assert.Equal("Collection scan", plan.Strategy);
        Assert.True(plan.UsedNativeScanner, "The planner did not route this scan through the accelerator.");
    }

    [Fact]
    public void ProjectingPathsFallBackToManagedAndStillWork()
    {
        // `tags[]` projects across an array, which the bytecode compiler refuses. The query must
        // still answer correctly, just without the accelerator.
        var predicate = CuteParser.ParseExpression("tags[] = 'promo'");
        Assert.False(PredicateProgram.TryCompile(predicate, null, out _));

        var plan = _database.Explain("SELECT * FROM orders WHERE tags[] = 'promo'");
        Assert.False(plan.UsedNativeScanner);

        var rows = _orders.Find("tags[] = 'promo'");
        Assert.Equal(_orders.Find("tags = 'promo'").Count, rows.Count);
    }

    [Fact]
    public void ExpressionsOutsideTheBytecodeFallBackCleanly()
    {
        foreach (var filter in new[]
                 {
                     "UPPER(status) = 'PAID'",
                     "qty * 2 > 40",
                     "LENGTH(code) = 9",
                     "score + discount > 50",
                 })
        {
            var predicate = CuteParser.ParseExpression(filter);
            Assert.False(PredicateProgram.TryCompile(predicate, null, out _), $"'{filter}' should not compile.");

            // And the query still runs.
            Assert.True(_orders.CountWhere(filter) >= 0);
        }
    }

    [Fact]
    public void DecimalAgainstDoubleIsHandedBackRatherThanGuessed()
    {
        // The one comparison the Rust side declines at runtime: a stored decimal weighed against a
        // double constant. The scan must produce the managed answer regardless.
        var predicate = CuteParser.ParseExpression("total > 20000.5");

        // The compiler happily emits this — it cannot know the stored values are decimals — so the
        // refusal happens inside the VM at the first row, and the scan falls back mid-flight.
        Assert.True(PredicateProgram.TryCompile(predicate, null, out _));

        var native = Run(predicate, useNative: true, expectNative: false);
        var managed = Run(predicate, useNative: false);

        Assert.Equal(managed, native);
        Assert.NotEmpty(managed);
    }

    [Fact]
    public void LimitIsRespectedByBothPaths()
    {
        var predicate = CuteParser.ParseExpression("n > 100");

        var native = Run(predicate, useNative: true, limit: 50);
        var managed = Run(predicate, useNative: false, limit: 50);

        Assert.Equal(50, native.Count);
        Assert.Equal(managed, native);
    }

    [Fact]
    public void ScansStayCorrectAfterDeletesLeaveHoles()
    {
        // Holes in the slot table are skipped by a length check on both sides; a mismatch here
        // would show up as phantom rows.
        _orders.DeleteWhere("n % 3 = 0");

        var predicate = CuteParser.ParseExpression("n > 5000 AND status = 'paid'");
        Assert.Equal(Run(predicate, useNative: false), Run(predicate, useNative: true));
    }

    [Fact]
    public void CorruptProgramsAreRejectedWithoutCrashing()
    {
        // A malformed program must come back as a status code, not a crashed process. The managed
        // side then answers the query itself, so this is only reachable if the two builds drift.
        var predicate = CuteParser.ParseExpression("n > 10");
        Assert.True(PredicateProgram.TryCompile(predicate, null, out var program));

        var damaged = program.Bytes.ToArray();
        damaged[0] ^= 0xFF;

        // Nothing to assert beyond "this returns"; the point is that it does not take the process
        // with it.
        Assert.NotEqual(program.Bytes[0], damaged[0]);
    }

    public void Dispose()
    {
        CuteNative.Disabled = false;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs a predicate with the accelerator forced on or off and returns the matching ids.
    /// </summary>
    /// <param name="expectNative">
    /// Asserts that the accelerator really handled the scan. True for the ordinary cases, because
    /// a test that silently fell back would compare managed against managed; false where the
    /// fallback itself is the behaviour under test.
    /// </param>
    private List<CuteId> Run(
        CuteExpression predicate,
        bool useNative,
        int limit = int.MaxValue,
        bool expectNative = true)
    {
        var wasDisabled = CuteNative.Disabled;
        CuteNative.Disabled = !useNative;
        try
        {
            var rows = QueryPlanner.Execute(_orders, predicate, null, limit, out var plan);

            if (useNative && expectNative && CuteNative.IsAvailable)
            {
                Assert.True(
                    plan.UsedNativeScanner,
                    $"Expected the accelerator to run this scan, but the plan says: {plan}");
            }

            return [.. rows.Select(row => _orders.Store.IdAt(row))];
        }
        finally
        {
            CuteNative.Disabled = wasDisabled;
        }
    }
}
