using System.Text;
using MemSharp.Cli;
using MemSharp.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

// Spectre draws with box-drawing characters and the banner uses a Figlet font; on Windows the
// console defaults to a code page that renders both as mojibake.
if (OperatingSystem.IsWindows())
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
    }
    catch (IOException)
    {
        // Output is redirected to something that has no encoding to set - a pipe or a file. The
        // characters still come out as UTF-8 bytes; only an interactive console needed telling.
    }
}

// `--version` before the parser sees it: Spectre's strict parsing rejects unknown options, and a
// CLI that cannot report its own version is missing something everyone reasonably expects.
if (args.Length == 1 && args[0] is "--version" or "-v")
{
    var assembly = typeof(Theme).Assembly;
    var version = assembly.GetName().Version;
    AnsiConsole.MarkupLine(
        $"[{Theme.Accent}]memsharp[/] [{Theme.Value}]{version?.ToString(3) ?? "unknown"}[/]  " +
        $"[{Theme.Muted}]Gravicode Studios, led by Kang Fadhil[/]");
    return 0;
}

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("memsharp");
    config.UseStrictParsing();
    config.ValidateExamples();

    config.AddCommand<ReplCommand>("repl")
        .WithDescription("Interactive shell against an embedded database or a running server.")
        .WithExample("repl")
        .WithExample("repl", "--data", "trading.msnap", "--sync", "auto")
        .WithExample("repl", "--connect", "127.0.0.1:6380")
        .WithExample("repl", "-e", "SET price 100", "-e", "GET price");

    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Host a RESP server with a live status display.")
        .WithExample("serve")
        .WithExample("serve", "--port", "6380", "--data", "trading.msnap", "--sync", "auto", "--aof");

    config.AddCommand<BrowseCommand>("browse")
        .WithDescription("Inspect a keyspace or a snapshot file.")
        .WithExample("browse", "--data", "trading.msnap")
        .WithExample("browse", "order:*", "--data", "trading.msnap", "--values");

    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Measure throughput and latency percentiles.")
        .WithExample("bench")
        .WithExample("bench", "--tcp", "--pipeline", "16")
        .WithExample("bench", "-n", "1000000", "--json", "benchmarks/results.json");

    config.AddCommand<DemoCommand>("demo")
        .WithDescription("A guided tour of every feature, with the code that produced each result.")
        .WithExample("demo")
        .WithExample("demo", "--step");

    config.SetExceptionHandler((exception, _) =>
    {
        AnsiConsole.MarkupLine($"[{Theme.Danger}]error[/] {exception.Message.EscapeMarkup()}");
        if (Environment.GetEnvironmentVariable("MEMSHARP_DEBUG") == "1")
        {
            AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
        }
        return 1;
    });
});

return app.Run(args);
