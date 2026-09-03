using System.ComponentModel;
using System.Diagnostics;
using CuteDB.Retail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>Fills a database with the Nusantara Retail sample dataset.</summary>
internal sealed class SeedCommand : DatabaseCommand<SeedCommand.Settings>
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandOption("-s|--scale <SCALE>")]
        [Description("tiny, demo (default), large or huge.")]
        public string Scale { get; init; } = "demo";

        [CommandOption("--orders <COUNT>")]
        [Description("Override the number of orders.")]
        public int? Orders { get; init; }

        [CommandOption("--force")]
        [Description("Seed even when the database already holds documents.")]
        public bool Force { get; init; }
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var scale = ParseScale(settings.Scale);
        if (settings.Orders is { } orders)
        {
            scale = scale with { Orders = orders };
        }

        var existing = database.Stats().DocumentCount;
        if (existing > 0 && !settings.Force)
        {
            throw new CuteDbException(
                $"'{settings.Database}' already holds {existing:N0} documents. " +
                "Seeding would add to them; pass --force if that is what you want.");
        }

        Theme.WriteHeading(console, "Nusantara Retail");
        console.MarkupLine(
            $"[{Theme.Muted}]Membuat[/] [{Theme.Pandan}]{scale.TotalDocuments:N0}[/] " +
            $"[{Theme.Muted}]dokumen: {scale.Customers:N0} pelanggan, {scale.Products:N0} produk, " +
            $"{scale.Orders:N0} pesanan, {RetailScale.StoreCount} toko.[/]");
        console.WriteLine();

        var timer = Stopwatch.StartNew();

        console.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn
                {
                    CompletedStyle = new Style(foreground: Color.FromHex(Theme.Pandan)),
                    FinishedStyle = new Style(foreground: Color.FromHex(Theme.Pandan)),
                    RemainingStyle = new Style(foreground: Color.FromHex("#2a2735")),
                },
                new PercentageColumn(),
                new SpinnerColumn(Spinner.Known.Line) { Style = new Style(foreground: Color.FromHex(Theme.Gold)) })
            .Start(context =>
            {
                // Each stage is one task rather than one bar over the whole run: the generator
                // works collection by collection and a single percentage across four different
                // unit costs would be a lie.
                var stages = new Dictionary<string, ProgressTask>(StringComparer.Ordinal)
                {
                    ["stores"] = context.AddTask($"[{Theme.Ink}]toko[/]", maxValue: RetailScale.StoreCount),
                    ["products"] = context.AddTask($"[{Theme.Ink}]produk[/]", maxValue: scale.Products),
                    ["customers"] = context.AddTask($"[{Theme.Ink}]pelanggan[/]", maxValue: scale.Customers),
                    ["orders"] = context.AddTask($"[{Theme.Ink}]pesanan[/]", maxValue: scale.Orders),
                    ["indexes"] = context.AddTask($"[{Theme.Ink}]indeks[/]", maxValue: 4),
                };

                NusantaraRetail.Seed(database, scale, (stage, done, total) =>
                {
                    if (stages.TryGetValue(stage, out var task))
                    {
                        task.MaxValue = total;
                        task.Value = done;
                    }
                });

                foreach (var task in stages.Values)
                {
                    task.Value = task.MaxValue;
                }
            });

        database.Flush(durable: true);
        timer.Stop();

        var stats = database.Stats();
        console.WriteLine();
        Theme.WriteSuccess(
            console,
            $"{stats.DocumentCount:N0} dokumen dalam {Theme.FormatDuration(timer.Elapsed)} " +
            $"({stats.DocumentCount / timer.Elapsed.TotalSeconds:N0}/detik) · " +
            $"berkas {Theme.FormatBytes(stats.FileBytes)}");

        console.WriteLine();
        Theme.WriteNote(console, "Coba / try:");
        foreach (var (title, query, _) in NusantaraRetail.ShowcaseQueries.Take(3))
        {
            console.MarkupLine($"  [{Theme.Muted}]{title.EscapeMarkup()}[/]");
            console.MarkupLine($"  [{Theme.Gold}]cutedb query {settings.Database.EscapeMarkup()} \"{query.EscapeMarkup()}\"[/]");
            console.WriteLine();
        }

        return 0;
    }

    private static RetailScale ParseScale(string scale) => scale.ToLowerInvariant() switch
    {
        "tiny" => RetailScale.Tiny,
        "demo" => RetailScale.Demo,
        "large" => RetailScale.Large,
        "huge" => RetailScale.Huge,
        _ => throw new CuteDbException($"'{scale}' is not a scale. Use tiny, demo, large or huge."),
    };
}

/// <summary>Reclaims space by rewriting the file with only current state.</summary>
internal sealed class CompactCommand : DatabaseCommand<DatabaseSettings>
{
    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, DatabaseSettings settings)
    {
        var before = database.Stats();

        if (before.FileAmplification < 1.2 && before.DeadBytes == 0)
        {
            Theme.WriteNote(
                console,
                $"Sudah padat. / Already compact ({Theme.FormatBytes(before.FileBytes)}, " +
                $"{before.FileAmplification:N1}× live data). Nothing to do.");
            return 0;
        }

        long reclaimed = 0;
        console.Status()
            .Spinner(Spinner.Known.Line)
            .SpinnerStyle(new Style(foreground: Color.FromHex(Theme.Gold)))
            .Start($"[{Theme.Ink}]Menulis ulang berkas…[/]", _ => reclaimed = database.Compact());

        var after = database.Stats();
        Theme.WriteSuccess(
            console,
            $"{Theme.FormatBytes(before.FileBytes)} → {Theme.FormatBytes(after.FileBytes)} " +
            $"(menghemat {Theme.FormatBytes(reclaimed)})");

        return 0;
    }
}
