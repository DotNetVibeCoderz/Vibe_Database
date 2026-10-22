namespace CuteDB.Cli.Tests;

/// <summary><c>cutedb query</c>, in each output format, and its failure modes.</summary>
public sealed class QueryCommandTests(SeededDatabase seeded) : IClassFixture<SeededDatabase>
{
    private const string TopCities =
        "SELECT address.city AS city, COUNT(*) AS n FROM orders GROUP BY address.city ORDER BY n DESC LIMIT 5";

    [Fact]
    public void Table_prints_the_columns_and_a_footer()
    {
        var run = Cli.Ok("query", seeded.Path, TopCities);

        Assert.Contains("city", run.Output);
        Assert.Contains("5 baris", run.Output);
    }

    [Fact]
    public void Max_rows_limits_what_is_printed_not_what_runs()
    {
        var run = Cli.Ok("query", seeded.Path, "SELECT code FROM stores", "-n", "3");

        Assert.Contains("3 dari 24 baris", run.Output);
    }

    [Fact]
    public void Json_is_a_parseable_array_with_no_banner()
    {
        var run = Cli.Ok("query", seeded.Path, TopCities, "--format", "json");

        // Data formats suppress the banner on their own: it shares the stream with the data, and
        // `> result.json` would otherwise start the file with the wordmark.
        Assert.StartsWith("[", run.Output.TrimStart());
        var rows = CuteJson.Parse(run.Output);
        Assert.True(rows.IsArray);
        Assert.Equal(5, rows.Count);

        var expected = Engine.Read(seeded.Path, db => db.Execute(TopCities));
        Assert.Equal(expected.Rows[0]["city"].AsString, rows[0]["city"].AsString);
        Assert.Equal(expected.Rows[0]["n"].AsInt64, rows[0]["n"].AsInt64);
    }

    [Fact]
    public void Json_lines_is_one_object_per_line()
    {
        var run = Cli.Ok("query", seeded.Path, "SELECT code, address.city AS city FROM stores", "-f", "jsonl");

        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(24, lines.Length);
        Assert.All(lines, line => Assert.True(CuteJson.Parse(line).IsObject, line));
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("json")]
    [InlineData("csv")]
    public void Data_formats_are_not_wrapped_at_the_console_width(string format)
    {
        // A redirected console is 80 columns. An order is several hundred characters of JSON, and
        // data written through Spectre's layout used to be broken across lines at column 80.
        var run = Cli.RunAt(80, "query", seeded.Path, "SELECT * FROM orders ORDER BY code LIMIT 5", "-f", format);
        Assert.Equal(0, run.ExitCode);

        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        switch (format)
        {
            case "jsonl":
                Assert.Equal(5, lines.Count);
                Assert.All(lines, line => Assert.Equal(CuteType.Object, CuteJson.Parse(line).Type));
                break;

            case "json":
                Assert.Equal(5, CuteJson.Parse(run.Output).Count);
                break;

            case "csv":
                Assert.Equal(6, lines.Count);
                Assert.True(lines[1].Length > 80);
                break;
        }
    }

    [Fact]
    public void Csv_has_a_header_and_quotes_only_what_needs_it()
    {
        var run = Cli.Ok("query", seeded.Path, "SELECT code, name, address FROM stores ORDER BY code LIMIT 2", "-f", "csv");

        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("code,name,address", lines[0]);
        Assert.StartsWith("ST-001,Nusantara Jakarta Pusat,\"{", lines[1]);
    }

    [Fact]
    public void Parameters_bind_without_becoming_syntax()
    {
        var run = Cli.Ok(
            "query", seeded.Path, "SELECT COUNT(*) AS n FROM orders WHERE address.city = @city AND total > @min",
            "-p", "city=Bandung", "-p", "min=100000", "-f", "json");

        var expected = Engine.Read(seeded.Path, db =>
            db.Collection("orders").Find("address.city = 'Bandung' AND total > 100000").Count);

        Assert.Equal(expected, CuteJson.Parse(run.Output)[0]["n"].AsInt64);
    }

    [Fact]
    public void The_alias_q_works()
        => Cli.Ok("q", seeded.Path, "SELECT * FROM stores LIMIT 1", "--quiet");

    [Fact]
    public void A_write_runs_and_reports_what_it_changed()
    {
        using var directory = new TempDirectory();
        var path = seeded.CopyTo(directory);

        var run = Cli.Ok("query", path, "DELETE FROM orders WHERE status = 'dibatalkan'");

        Assert.Contains("dihapus / deleted", run.Output);
        Assert.Equal(0, Engine.Read(path, db => db.Collection("orders").Find("status = 'dibatalkan'").Count));
    }

    [Fact]
    public void A_bad_query_exits_non_zero_with_the_caret()
    {
        var run = Cli.Fails("query", seeded.Path, "SELECT * FROM orders WHERE total ~ 5");

        Assert.Contains("'~' does not belong in a query", run.Output);
        Assert.Contains("^", run.Output);
    }

    [Fact]
    public void An_unknown_format_is_rejected()
    {
        var run = Cli.Fails("query", seeded.Path, "SELECT * FROM orders", "--format", "xml");

        Assert.Contains("'xml' is not an output format", run.Output);
    }

    [Fact]
    public void A_malformed_parameter_is_rejected()
    {
        var run = Cli.Fails("query", seeded.Path, "SELECT * FROM orders WHERE total > @min", "-p", "min");

        Assert.Contains("Write it as name=value", run.Output);
    }

    [Fact]
    public void A_select_against_a_missing_file_does_not_create_it()
    {
        using var directory = new TempDirectory();
        var path = directory.File("typo.cute");

        var run = Cli.Fails("query", path, "SELECT * FROM orders");

        Assert.Contains("There is no CuteDB database", run.Output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_insert_against_a_missing_file_creates_it()
    {
        // The other half of the read-only rule: a statement that writes is allowed to create the
        // database, as it always has, so `cutedb query new.cute "INSERT …"` still starts one.
        using var directory = new TempDirectory();
        var path = directory.File("new.cute");

        Cli.Ok("query", path, "INSERT INTO notes VALUES { 'title': 'halo' }");

        Assert.Equal(1, Engine.Count(path, "notes"));
    }

    [Fact]
    public void An_unknown_option_is_a_parse_error_not_a_crash()
    {
        var run = Cli.Run("query", seeded.Path, "SELECT * FROM orders", "--no-such-flag");

        Assert.NotEqual(0, run.ExitCode);
        Assert.DoesNotContain("   at ", run.Output);
    }
}
