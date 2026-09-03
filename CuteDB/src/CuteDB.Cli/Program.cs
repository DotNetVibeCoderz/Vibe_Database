using CuteDB.Cli;
using CuteDB.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("cutedb");
    config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    config.UseStrictParsing();
    config.CaseSensitivity(CaseSensitivity.None);

    // Spectre's own exception rendering prints a stack trace, which is the wrong thing to show
    // someone who mistyped a query. Every command routes its failures through Theme.WriteError
    // instead; this catches anything that escapes.
    config.SetExceptionHandler((exception, _) =>
    {
        Theme.WriteError(AnsiConsole.Console, exception);
        return 1;
    });

    config.AddCommand<InfoCommand>("info")
        .WithDescription("Show what is in a database: collections, indexes, size and memory.")
        .WithExample("info", "shop.cute");

    config.AddCommand<QueryCommand>("query")
        .WithAlias("q")
        .WithDescription("Run one CuteQL statement and print the result.")
        .WithExample("query", "shop.cute", "\"SELECT * FROM orders LIMIT 10\"")
        .WithExample("query", "shop.cute", "\"SELECT city, SUM(total) FROM orders GROUP BY city\"", "--format", "json");

    config.AddCommand<ShellCommand>("shell")
        .WithDescription("Open an interactive CuteQL session.")
        .WithExample("shell", "shop.cute");

    config.AddCommand<SeedCommand>("seed")
        .WithDescription("Fill a database with the Nusantara Retail sample dataset.")
        .WithExample("seed", "shop.cute", "--scale", "demo");

    config.AddCommand<ImportCommand>("import")
        .WithDescription("Load documents from JSON, JSON Lines or CSV.")
        .WithExample("import", "shop.cute", "orders.jsonl", "--collection", "orders");

    config.AddCommand<ExportCommand>("export")
        .WithDescription("Write a collection out as JSON, JSON Lines or CSV.")
        .WithExample("export", "shop.cute", "orders", "--out", "orders.jsonl", "--format", "jsonl");

    config.AddBranch("index", index =>
    {
        index.SetDescription("Create, list and drop secondary indexes.");

        index.AddCommand<IndexListCommand>("list")
            .WithDescription("List the indexes on a collection.")
            .WithExample("index", "list", "shop.cute", "orders");

        index.AddCommand<IndexCreateCommand>("create")
            .WithDescription("Create an index over a document path.")
            .WithExample("index", "create", "shop.cute", "orders", "address.city");

        index.AddCommand<IndexDropCommand>("drop")
            .WithDescription("Drop an index by name.")
            .WithExample("index", "drop", "shop.cute", "orders", "address.city");
    });

    config.AddCommand<CompactCommand>("compact")
        .WithDescription("Rewrite the file with only current state, reclaiming space.")
        .WithExample("compact", "shop.cute");

    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Measure insert, scan, index and lookup throughput on this machine.")
        .WithExample("bench", "--rows", "500000");
});

return await app.RunAsync(args);

/// <summary>Entry point marker, so the assembly version can be found from top-level statements.</summary>
public partial class Program;
