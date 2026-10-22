using System.Text;

namespace CuteDB.Cli.Tests;

/// <summary>
/// <c>export</c> then <c>import</c> into a fresh file, and what survives the trip in each format.
/// </summary>
/// <remarks>
/// <para>
/// Each format makes a different promise, and the assertions are exactly that promise — no
/// looser, so a regression shows, and no stricter, so the test does not demand what the format
/// cannot carry:
/// </para>
/// <list type="bullet">
/// <item><b>JSON / JSON Lines with <c>--lossless</c></b>: identical. Every value, every type, every
/// id, field order included.</item>
/// <item><b>JSON / JSON Lines, plain, imported with <c>--decimal</c></b>: identical as JSON. Types
/// JSON has no word for come back as what JSON wrote: dates as ISO-8601 strings, decimals as
/// numbers (narrowed to an integer when whole, because JSON does not keep a trailing ".00").
/// Ids survive as ids.</item>
/// <item><b>CSV</b>: one row per document, one column per top-level field. Scalars come back by
/// value through type inference; nested objects and arrays travel as JSON inside the cell and come
/// back as structure; <c>null</c> and missing both become an empty cell and come back missing.</item>
/// </list>
/// </remarks>
public sealed class ImportExportTests(SeededDatabase seeded) : IClassFixture<SeededDatabase>
{
    private static readonly string[] Collections = ["orders", "customers", "products", "stores"];

    // ---- JSON, lossless: exact -----------------------------------------------------------------

    [Theory]
    [InlineData("jsonl")]
    [InlineData("json")]
    public void Lossless_round_trip_is_exact(string format)
    {
        using var directory = new TempDirectory();
        var target = directory.File("restored.cute");

        foreach (var collection in Collections)
        {
            var file = directory.File($"{collection}.{format}");
            Cli.Ok("export", seeded.Path, collection, "--out", file, "--lossless");
            Cli.Ok("import", target, file, "--collection", collection);

            var original = Engine.Documents(seeded.Path, collection);
            var restored = Engine.Documents(target, collection);

            Assert.Equal(original.Count, restored.Count);
            foreach (var (id, document) in original)
            {
                Assert.True(restored.TryGetValue(id, out var back), $"{collection} {id} did not come back");
                Assert.Equal(Engine.Lossless(document), Engine.Lossless(back));
            }
        }
    }

    // ---- JSON, plain: equal as JSON ------------------------------------------------------------

    [Theory]
    [InlineData("jsonl")]
    [InlineData("json")]
    public void Plain_round_trip_is_equal_as_json(string format)
    {
        using var directory = new TempDirectory();
        var target = directory.File("restored.cute");
        var file = directory.File($"orders.{format}");

        Cli.Ok("export", seeded.Path, "orders", "--out", file);
        Cli.Ok("import", target, file, "--collection", "orders", "--decimal");

        var original = Engine.Documents(seeded.Path, "orders");
        var restored = Engine.Documents(target, "orders");
        Assert.Equal(original.Count, restored.Count);

        foreach (var (id, document) in original)
        {
            var back = restored[id];
            Assert.Equal(document.ToJson(), back.ToJson());

            // The id is an id again, not the hex string JSON carried it as — otherwise an
            // `_id = …` lookup, which compares against an id, would never match a restored row.
            Assert.Equal(CuteType.Id, back["_id"].Type);

            // The documented loss: a date becomes the string JSON wrote.
            Assert.Equal(CuteType.DateTime, document["placedAt"].Type);
            Assert.Equal(CuteType.String, back["placedAt"].Type);

            // Money survives exactly under --decimal: no double rounding on the way in.
            Assert.Equal(Number(document["total"]), Number(back["total"]));
        }
    }

    [Fact]
    public void Without_decimal_money_arrives_as_double()
    {
        using var directory = new TempDirectory();
        var file = directory.File("products.jsonl");
        var target = directory.File("restored.cute");

        Cli.Ok("export", seeded.Path, "products", "--out", file);
        var run = Cli.Ok("import", target, file, "--collection", "products");

        Assert.Contains("--decimal", run.Output);
        var fractional = Engine.Documents(target, "products").Values.First(d => d["price"].Type != CuteType.Int32 && d["price"].Type != CuteType.Int64);
        Assert.Equal(CuteType.Double, fractional["price"].Type);
    }

    // ---- CSV: by value, with the documented limits ---------------------------------------------

    [Theory]
    [InlineData("orders")]
    [InlineData("customers")]
    [InlineData("products")]
    [InlineData("stores")]
    public void Csv_round_trip_keeps_values(string collection)
    {
        using var directory = new TempDirectory();
        var file = directory.File($"{collection}.csv");
        var target = directory.File("restored.cute");

        Cli.Ok("export", seeded.Path, collection, "--out", file);
        Cli.Ok("import", target, file, "--collection", collection, "--decimal");

        var original = Engine.Documents(seeded.Path, collection);
        var restored = Engine.Documents(target, collection);
        Assert.Equal(original.Count, restored.Count);

        foreach (var (id, document) in original)
        {
            var back = restored[id];

            foreach (var (field, value) in document)
            {
                AssertSurvivesCsv(value, back[field], $"{collection} {id} .{field}");
            }

            // Nothing invented: every restored field was in the original.
            foreach (var (field, _) in back)
            {
                Assert.False(document[field].IsMissing, $"{collection} {id} gained .{field}");
            }
        }
    }

    /// <summary>What CSV promises for one top-level value, and no more.</summary>
    private static void AssertSurvivesCsv(CuteValue original, CuteValue back, string where)
    {
        switch (original.Type)
        {
            // An empty cell cannot say which of the two it was.
            case CuteType.Null:
                Assert.True(back.IsMissing, $"{where}: null should come back missing, got {back.Type}");
                break;

            // Numbers come back by value. A whole decimal ("0", "225101") is read as an integer,
            // because nothing in a CSV cell says it was a decimal.
            case CuteType.Int32 or CuteType.Int64 or CuteType.Double or CuteType.Decimal:
                Assert.True(back.IsNumber, $"{where}: {original} came back as {back.Type}");
                Assert.Equal(Number(original), Number(back));
                break;

            // Written in the round-trip ISO form, so it comes back to the tick.
            case CuteType.DateTime:
                Assert.Equal(CuteType.DateTime, back.Type);
                Assert.Equal(original.AsDateTime, back.AsDateTime);
                break;

            // A string spelled like a number — a barcode, say — is read as that number: a cell has
            // no quotes that mean "text". The digits survive; the type does not. A leading zero or
            // plus is the exception, and stays text (Csv_keeps_awkward_strings_as_they_were).
            case CuteType.String when back.Type != CuteType.String:
                Assert.True(back.IsNumber || back.Type is CuteType.True or CuteType.False,
                    $"{where}: \"{original.AsString}\" came back as {back.Type}");
                Assert.Equal(original.AsString, back.ToDisplayString(), StringComparer.OrdinalIgnoreCase);
                break;

            // Structure travels as JSON in the cell; it is equal as JSON, with the same limits as
            // a plain JSON export (a nested date comes back as its string).
            case CuteType.Object or CuteType.Array:
                Assert.Equal(original.Type, back.Type);
                Assert.Equal(CuteJson.Write(original), CuteJson.Write(back));
                break;

            default:
                Assert.Equal(CuteJson.Write(original, CuteJsonOptions.Lossless), CuteJson.Write(back, CuteJsonOptions.Lossless));
                break;
        }
    }

    /// <summary>A number's value, whatever type carries it — exactly, for the decimal case.</summary>
    private static decimal Number(CuteValue value) => value.Type switch
    {
        CuteType.Decimal => value.AsDecimal,
        CuteType.Int32 or CuteType.Int64 => value.AsInt64,
        CuteType.Double => (decimal)value.AsDouble,
        _ => throw new InvalidOperationException($"{value.Type} is not a number"),
    };

    [Fact]
    public void Csv_keeps_awkward_strings_as_they_were()
    {
        using var directory = new TempDirectory();
        var source = directory.File("source.cute");

        // Each of these broke, or would break, a naive CSV reader or a greedy type inference.
        string[] awkward =
        [
            "comma, inside",
            "a \"quoted\" word",
            "line one\nline two",
            "0812345678",          // a phone number: the leading zero is the data
            "+6281234",            // so is the plus
            "007",
            "[draft] not json",
            "{not json either",
            "  padded  ",
            "TRUE story",
            "12:30",
        ];

        using (var database = CuteDatabase.Open(source))
        {
            var notes = database.Collection("notes");
            foreach (var text in awkward)
            {
                notes.Insert(new CuteDocument().Set("text", CuteValue.String(text)));
            }
        }

        var file = directory.File("notes.csv");
        var target = directory.File("restored.cute");
        Cli.Ok("export", source, "notes", "--out", file);
        Cli.Ok("import", target, file);

        var restored = Engine.Documents(target, "notes").Values.Select(d => d["text"]).ToList();
        foreach (var text in awkward)
        {
            Assert.Contains(restored, value => value.Type == CuteType.String && value.AsString == text);
        }
    }

    [Fact]
    public void Csv_infers_types_for_plain_values()
    {
        using var directory = new TempDirectory();
        var file = directory.File("people.csv");
        File.WriteAllText(file,
            "name,age,score,big,active,joined,note\n" +
            "Rina,31,4.5,9000000000,true,2026-03-01T12:00:00Z,\n" +
            "\"Budi, Jr.\",0,-1.25,1,false,2026-03-02,x\n");
        var target = directory.File("t.cute");

        Cli.Ok("import", target, file);

        var rows = Engine.Documents(target, "people").Values.OrderBy(d => d["name"].AsString).ToList();
        Assert.Equal("Budi, Jr.", rows[0]["name"].AsString);
        Assert.Equal(CuteType.Int32, rows[1]["age"].Type);
        Assert.Equal(CuteType.Int32, rows[0]["age"].Type);  // "0" alone is a number, not a code
        Assert.Equal(CuteType.Double, rows[1]["score"].Type);
        Assert.Equal(CuteType.Int64, rows[1]["big"].Type);
        Assert.Equal(CuteType.True, rows[1]["active"].Type);
        Assert.Equal(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), rows[1]["joined"].AsDateTime);
        Assert.True(rows[1]["note"].IsMissing, "an empty cell is a missing field");
    }

    // ---- export details -----------------------------------------------------------------------

    [Fact]
    public void Export_to_stdout_is_pure_data_and_imports_back()
    {
        using var directory = new TempDirectory();

        var run = Cli.Ok("export", seeded.Path, "stores");

        // No banner and no success line: both would land in `> stores.jsonl`.
        Assert.Equal(string.Empty, run.Output.Trim());
        var file = directory.File("stores.jsonl");
        File.WriteAllText(file, run.Stdout);

        var target = directory.File("restored.cute");
        Cli.Ok("import", target, file);
        Assert.Equal(24, Engine.Count(target, "stores"));
    }

    [Fact]
    public void Export_files_carry_no_byte_order_mark()
    {
        // RFC 8259 forbids one in JSON, and Python's json module rejects a file that has it.
        using var directory = new TempDirectory();
        foreach (var extension in new[] { "json", "jsonl", "csv" })
        {
            var file = directory.File($"stores.{extension}");
            Cli.Ok("export", seeded.Path, "stores", "--out", file);

            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{extension} starts with a BOM");
        }
    }

    [Fact]
    public void Export_where_filters_and_format_overrides_the_extension()
    {
        using var directory = new TempDirectory();
        var file = directory.File("big.txt");

        Cli.Ok("export", seeded.Path, "orders", "--out", file, "--format", "jsonl", "--where", "total > 1000000");

        var expected = Engine.Read(seeded.Path, db => db.Collection("orders").Find("total > 1000000").Count);
        var lines = File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Length > 0).ToList();
        Assert.Equal(expected, lines.Count);
    }

    [Fact]
    public void Export_of_a_missing_collection_lists_what_exists()
    {
        var run = Cli.Fails("export", seeded.Path, "ordres", "--out", "unused.jsonl");

        Assert.Contains("no collection called 'ordres'", run.Output);
        Assert.Contains("orders", run.Output);
    }

    // ---- import failures ----------------------------------------------------------------------

    [Fact]
    public void Import_of_a_missing_file_fails_cleanly()
    {
        using var directory = new TempDirectory();
        var run = Cli.Fails("import", directory.File("t.cute"), directory.File("nothing.jsonl"));

        Assert.Contains("There is no file", run.Output);
    }

    [Fact]
    public void Import_of_an_unknown_extension_asks_for_a_format()
    {
        using var directory = new TempDirectory();
        var file = directory.File("data.xml");
        File.WriteAllText(file, "<x/>");

        var run = Cli.Fails("import", directory.File("t.cute"), file);

        Assert.Contains("Pass --format json, jsonl or csv", run.Output);
    }

    [Fact]
    public void Import_of_malformed_json_lines_fails_cleanly()
    {
        using var directory = new TempDirectory();
        var file = directory.File("bad.jsonl");
        File.WriteAllText(file, "{\"ok\":1}\n{\"broken\":\n");

        var run = Cli.Fails("import", directory.File("t.cute"), file);

        Assert.Contains("Invalid JSON", run.Output);
    }

    [Fact]
    public void Import_of_a_non_object_line_says_what_it_found()
    {
        using var directory = new TempDirectory();
        var file = directory.File("bad.jsonl");
        File.WriteAllText(file, "[1,2,3]\n");

        var run = Cli.Fails("import", directory.File("t.cute"), file);

        Assert.Contains("Every line must be a JSON object", run.Output);
    }

    [Fact]
    public void Importing_the_same_ids_twice_is_refused()
    {
        using var directory = new TempDirectory();
        var file = directory.File("stores.jsonl");
        var target = directory.File("t.cute");
        Cli.Ok("export", seeded.Path, "stores", "--out", file);
        Cli.Ok("import", target, file);

        var run = Cli.Fails("import", target, file);

        Assert.Contains("already has a document with id", run.Output);
    }

    [Fact]
    public void Collection_name_defaults_to_the_file_name()
    {
        using var directory = new TempDirectory();
        var file = directory.File("toko.json");
        var target = directory.File("t.cute");
        Cli.Ok("export", seeded.Path, "stores", "--out", file);

        Cli.Ok("import", target, file);

        Assert.Equal(24, Engine.Count(target, "toko"));
    }
}
