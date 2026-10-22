namespace CuteDB.Cli.Tests;

/// <summary><c>seed</c>, <c>info</c>, <c>compact</c>, the <c>index</c> branch and <c>shell</c>.</summary>
public sealed class DatabaseCommandTests(SeededDatabase seeded) : IClassFixture<SeededDatabase>
{
    // ---- seed ---------------------------------------------------------------------------------

    [Fact]
    public void Seed_builds_the_documented_tiny_dataset()
    {
        // The counts are the ones docs/en/cli.md publishes for --scale tiny.
        Assert.Equal(1_000, Engine.Count(seeded.Path, "orders"));
        Assert.Equal(200, Engine.Count(seeded.Path, "customers"));
        Assert.Equal(120, Engine.Count(seeded.Path, "products"));
        Assert.Equal(24, Engine.Count(seeded.Path, "stores"));

        var indexes = Engine.Read(seeded.Path, db => db.Collection("orders").Indexes.Select(i => i.Name).ToList());
        Assert.Contains("orders_city", indexes);
        Assert.Contains("orders_status", indexes);
    }

    [Fact]
    public void Seed_reports_what_it_made()
    {
        using var directory = new TempDirectory();
        var run = Cli.Ok("seed", directory.File("s.cute"), "--scale", "tiny", "--orders", "50");

        Assert.Contains("Nusantara Retail", run.Output);
        Assert.Contains("394 dokumen", run.Output);
        Assert.Equal(50, Engine.Count(directory.File("s.cute"), "orders"));
    }

    [Fact]
    public void Seed_refuses_to_add_to_a_populated_database()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var run = Cli.Fails("seed", path, "--scale", "tiny");

        Assert.Contains("already holds 1,344 documents", run.Output);
        Assert.Equal(1_000, Engine.Count(path, "orders"));
    }

    [Fact]
    public void Seed_rejects_an_unknown_scale()
    {
        using var directory = new TempDirectory();
        var run = Cli.Fails("seed", directory.File("s.cute"), "--scale", "enormous");

        Assert.Contains("'enormous' is not a scale", run.Output);
    }

    // ---- info ---------------------------------------------------------------------------------

    [Fact]
    public void Info_lists_collections_counts_and_indexes()
    {
        var run = Cli.Ok("info", seeded.Path);

        Assert.Contains("1,344", run.Output);
        foreach (var collection in new[] { "orders", "customers", "products", "stores" })
        {
            Assert.Contains(collection, run.Output);
        }

        Assert.Contains("address.city", run.Output);
        Assert.Contains("scanner:", run.Output);
    }

    [Fact]
    public void Info_on_a_missing_file_says_so_and_creates_nothing()
    {
        using var directory = new TempDirectory();
        var path = directory.File("shpo.cute");

        var run = Cli.Fails("info", path);

        Assert.Contains("There is no CuteDB database", run.Output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_unknown_durability_mode_is_rejected()
    {
        var run = Cli.Fails("info", seeded.Path, "--durability", "sometimes");

        Assert.Contains("is not a durability mode", run.Output);
    }

    // ---- compact ------------------------------------------------------------------------------

    [Fact]
    public void Compact_reclaims_space_after_deletes()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);
        Cli.Ok("query", path, "DELETE FROM orders WHERE total > 0");
        var before = new FileInfo(path).Length;

        Cli.Ok("compact", path);

        Assert.True(new FileInfo(path).Length < before);
        Assert.Equal(200, Engine.Count(path, "customers"));
    }

    // ---- index --------------------------------------------------------------------------------

    [Fact]
    public void Index_create_list_explain_drop()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var created = Cli.Ok("index", "create", path, "customers", "loyalty.tier", "--name", "customers_tier");
        Assert.Contains("customers_tier", created.Output);

        var listed = Cli.Ok("index", "list", path, "customers");
        Assert.Contains("customers_tier", listed.Output);
        Assert.Contains("loyalty.tier", listed.Output);

        // The planner picks it up, which is the reason to create one.
        var plan = CuteJson.Parse(Cli.Ok("explain", path, "SELECT * FROM customers WHERE loyalty.tier = 'gold'", "-f", "json").Output);
        Assert.Equal("customers_tier", plan["index"].AsString);

        Cli.Ok("index", "drop", path, "customers", "customers_tier");
        Assert.DoesNotContain("customers_tier", Cli.Ok("index", "list", path, "customers").Output);

        var after = CuteJson.Parse(Cli.Ok("explain", path, "SELECT * FROM customers WHERE loyalty.tier = 'gold'", "-f", "json").Output);
        Assert.Equal("Collection scan", after["strategy"].AsString);
    }

    [Fact]
    public void Index_create_warns_when_no_document_has_the_path()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var run = Cli.Ok("index", "create", path, "orders", "adress.city");

        Assert.Contains("No document has a value at 'adress.city'", run.Output);
    }

    [Fact]
    public void A_unique_index_over_duplicates_fails_cleanly()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var run = Cli.Fails("index", "create", path, "orders", "status", "--unique");

        Assert.DoesNotContain("status", Engine.Read(path, db => db.Collection("orders").Indexes.Where(i => i.Unique).Select(i => i.Path).ToList()));
        Assert.NotEmpty(run.Output);
    }

    [Fact]
    public void Index_list_on_an_unindexed_collection_suggests_the_command()
    {
        var run = Cli.Ok("index", "list", seeded.Path, "stores");

        Assert.Contains("has no secondary indexes", run.Output);
        Assert.Contains("cutedb index create", run.Output);
    }

    [Fact]
    public void Index_drop_of_an_unknown_name_lists_what_exists()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var run = Cli.Fails("index", "drop", path, "orders", "nope");

        Assert.Contains("no index called 'nope'", run.Output);
        Assert.Contains("orders_city", run.Output);
    }

    [Fact]
    public void Index_commands_name_a_missing_collection()
    {
        var run = Cli.Fails("index", "list", seeded.Path, "ordres");

        Assert.Contains("no collection called 'ordres'", run.Output);
    }

    // ---- shell --------------------------------------------------------------------------------

    [Fact]
    public void Shell_runs_statements_and_meta_commands_from_piped_input()
    {
        var original = Console.In;
        Console.SetIn(new StringReader(
            "SELECT COUNT(*) AS n\n  FROM stores;\n" +
            "\\e SELECT * FROM orders WHERE address.city = 'Medan'\n" +
            "\\d\n" +
            "SELECT * FROM nowhere;\n" +
            "\\q\n"));

        try
        {
            var run = Cli.Ok("shell", seeded.Path, "--quiet");

            Assert.Contains("24", run.Output);                // the multi-line statement ran
            Assert.Contains("orders_city", run.Output);       // \e used the planner
            Assert.Contains("customers", run.Output);         // \d listed collections
            Assert.Contains("no collection called 'nowhere'", run.Output); // an error does not end the session
            Assert.Contains("Goodbye", run.Output);
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}
