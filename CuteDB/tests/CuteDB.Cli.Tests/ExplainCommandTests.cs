namespace CuteDB.Cli.Tests;

/// <summary><c>cutedb explain</c>.</summary>
public sealed class ExplainCommandTests(SeededDatabase seeded) : IClassFixture<SeededDatabase>
{
    private static CuteValue Json(CliRun run)
    {
        var value = CuteJson.Parse(run.Output);
        Assert.True(value.IsObject, run.ToString());
        return value;
    }

    [Fact]
    public void A_scan_reports_what_it_examined_and_matched()
    {
        var run = Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE total > 1000000", "--format", "json");
        var plan = Json(run);

        // The numbers have to agree with the engine, not merely look plausible.
        var (total, matched) = Engine.Read(seeded.Path, db => (
            db.Collection("orders").Count,
            db.Collection("orders").Find("total > 1000000").Count));

        Assert.Equal("orders", plan["collection"].AsString);
        Assert.Equal("Collection scan", plan["strategy"].AsString);
        Assert.True(plan["index"].IsNullOrMissing);
        Assert.Equal(total, plan["candidateRows"].AsInt32);
        Assert.Equal(matched, plan["matchedRows"].AsInt32);
        Assert.True(plan["returnedRows"].IsNullOrMissing, "returnedRows is only reported under --analyze");
    }

    [Fact]
    public void An_indexed_equality_is_an_index_seek()
    {
        // `seed` creates orders_city over address.city.
        var plan = Json(Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE address.city = 'Bandung'", "-f", "json"));

        var bandung = Engine.Read(seeded.Path, db => db.Collection("orders").Find("address.city = 'Bandung'").Count);

        Assert.Equal("Index seek", plan["strategy"].AsString);
        Assert.Equal("orders_city", plan["index"].AsString);
        Assert.Equal(bandung, plan["candidateRows"].AsInt32);
        Assert.Equal(bandung, plan["matchedRows"].AsInt32);
        Assert.False(plan["nativeScanner"].AsBoolean);
    }

    [Fact]
    public void Analyze_adds_rows_returned_and_time()
    {
        var plan = Json(Cli.Ok(
            "explain", seeded.Path,
            "SELECT address.city, COUNT(*) AS n FROM orders WHERE total > 100000 GROUP BY address.city",
            "--analyze", "-f", "json"));

        var cities = Engine.Read(seeded.Path, db => db.Execute(
            "SELECT address.city, COUNT(*) AS n FROM orders WHERE total > 100000 GROUP BY address.city").Rows.Count);

        // Matched is what passed the WHERE; returned is what came out of GROUP BY. They differ, and
        // the difference is the point of reporting both.
        Assert.Equal(cities, plan["returnedRows"].AsInt32);
        Assert.True(plan["matchedRows"].AsInt32 > cities);
        Assert.True(plan["engineMs"].AsDouble >= 0);
    }

    [Fact]
    public void Parameters_bind_as_they_do_for_query()
    {
        var plan = Json(Cli.Ok(
            "explain", seeded.Path, "SELECT * FROM orders WHERE address.city = @city",
            "-p", "city=Bandung", "-f", "json"));

        Assert.Equal("Index seek", plan["strategy"].AsString);
        Assert.True(plan["matchedRows"].AsInt32 > 0);
    }

    [Fact]
    public void The_table_shows_the_plan_panel()
    {
        var run = Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE status = 'dikirim'");

        Assert.Contains("rencana / plan", run.Output);
        Assert.Contains("Index seek", run.Output);
        Assert.Contains("orders_status", run.Output);
        Assert.Contains("examined", run.Output);
        Assert.Contains("matched", run.Output);
        Assert.Contains("--analyze", run.Output);
    }

    [Fact]
    public void Analyze_in_the_table_adds_returned_and_time()
    {
        var run = Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE total > 100000 LIMIT 3", "--analyze", "--quiet");

        Assert.Contains("returned", run.Output);
        Assert.Contains("engine time", run.Output);
        Assert.DoesNotContain("--analyze runs", run.Output);
    }

    [Fact]
    public void A_selective_scan_suggests_an_index()
    {
        var run = Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE units > 30", "--quiet");

        Assert.Contains("Collection scan", run.Output);
        Assert.Contains($"cutedb index create {seeded.Path} orders <path>", run.Output);
    }

    [Fact]
    public void An_index_that_would_return_most_rows_is_passed_over()
    {
        // 'selesai' is most orders. The planner declines a seek that returns over half the
        // collection, and explain has to show the scan it actually chose, not the index that exists.
        var plan = Json(Cli.Ok("explain", seeded.Path, "SELECT * FROM orders WHERE status = 'selesai'", "-f", "json"));

        Assert.Equal("Collection scan", plan["strategy"].AsString);
        Assert.True(plan["matchedRows"].AsInt32 > plan["candidateRows"].AsInt32 / 2);
    }

    [Fact]
    public void No_where_clause_is_a_full_collection_read()
    {
        var plan = Json(Cli.Ok("explain", seeded.Path, "SELECT * FROM stores", "-f", "json"));

        Assert.Equal("Full collection", plan["strategy"].AsString);
        Assert.Equal(24, plan["candidateRows"].AsInt32);
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE total > 0")]
    [InlineData("UPDATE orders SET status = 'x' WHERE total > 0")]
    [InlineData("INSERT INTO orders VALUES {code: 'X'}")]
    public void A_write_is_refused_and_never_runs(string statement)
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);
        var before = Engine.Count(path, "orders");
        var statuses = Engine.Read(path, db => db.Collection("orders").Find("status = 'x'").Count);

        // --analyze is the flag that executes, so it is the one that must not.
        var run = Cli.Fails("explain", path, statement, "--analyze");

        Assert.Contains("explain only plans a SELECT", run.Output);
        Assert.Equal(before, Engine.Count(path, "orders"));
        Assert.Equal(statuses, Engine.Read(path, db => db.Collection("orders").Find("status = 'x'").Count));
    }

    [Fact]
    public void A_missing_database_is_reported_and_not_created()
    {
        using var directory = new TempDirectory();
        var path = directory.File("nope.cute");

        var run = Cli.Fails("explain", path, "SELECT * FROM orders");

        Assert.Contains("There is no CuteDB database", run.Output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_bad_query_points_at_the_problem()
    {
        var run = Cli.Fails("explain", seeded.Path, "SELECT * FROM orders WHERE total ~ 5");

        Assert.Contains("'~' does not belong in a query", run.Output);
        Assert.Contains("^", run.Output);
    }

    [Fact]
    public void A_missing_collection_names_the_ones_that_exist()
    {
        var run = Cli.Fails("explain", seeded.Path, "SELECT * FROM ordres WHERE total > 5");

        Assert.Contains("no collection called 'ordres'", run.Output);
        Assert.Contains("orders", run.Output);
    }

    [Fact]
    public void An_unknown_format_is_rejected()
    {
        var run = Cli.Fails("explain", seeded.Path, "SELECT * FROM orders", "--format", "csv");

        Assert.Contains("is not an explain format", run.Output);
    }

    [Fact]
    public void Query_explain_renders_the_same_panel()
    {
        var run = Cli.Ok("query", seeded.Path, "SELECT * FROM orders WHERE address.city = 'Medan'", "--explain");

        Assert.Contains("rencana / plan", run.Output);
        Assert.Contains("orders_city", run.Output);
    }
}
