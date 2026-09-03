using System.ComponentModel;
using System.Diagnostics;
using CuteDB.Native;
using CuteDB.Retail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>
/// Measures CuteDB's throughput on the machine it is running on.
/// </summary>
/// <remarks>
/// <para>
/// Not a substitute for the BenchmarkDotNet suite in <c>benchmarks/</c>, which is what the
/// published figures come from. This exists so that someone evaluating CuteDB can get real numbers
/// for their own hardware in about thirty seconds, and so that a "why is this slow?" report can
/// start with a comparable measurement.
/// </para>
/// <para>
/// Every scan is run with the accelerator on and off, because the interesting number for most
/// people is not the absolute throughput but whether the native library loaded at all.
/// </para>
/// </remarks>
internal sealed class BenchCommand : Command<BenchCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--rows <COUNT>")]
        [Description("Orders to generate. Default 250000.")]
        public int Rows { get; init; } = 250_000;

        [CommandOption("--file <PATH>")]
        [Description("Benchmark against a file instead of memory, which also measures write durability.")]
        public string? File { get; init; }

        [CommandOption("--quiet")]
        [Description("Skip the banner.")]
        public bool Quiet { get; init; }
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var console = AnsiConsole.Console;
        if (!settings.Quiet)
        {
            Theme.WriteBanner(console);
        }

        var scale = RetailScale.Demo with
        {
            Orders = settings.Rows,
            Customers = Math.Max(1_000, settings.Rows / 10),
            Products = Math.Max(200, settings.Rows / 100),
        };

        console.MarkupLine(
            $"[{Theme.Muted}]{Environment.ProcessorCount} logical cores · " +
            $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}[/]");
        console.MarkupLine($"[{Theme.Muted}]scanner: {CuteNative.Describe().EscapeMarkup()}[/]");
        console.WriteLine();

        using var database = settings.File is null
            ? CuteDatabase.CreateInMemory()
            : CuteDatabase.Open(settings.File, CuteDatabaseOptions.Fast);

        var results = new List<(string Group, string Name, double Value, string Unit, string Note)>();

        // --- write path -------------------------------------------------------------------
        var timer = Stopwatch.StartNew();
        console.Status()
            .Spinner(Spinner.Known.Line)
            .SpinnerStyle(new Style(foreground: Color.FromHex(Theme.Gold)))
            .Start($"[{Theme.Ink}]Menyiapkan {scale.Orders:N0} pesanan…[/]", _ => NusantaraRetail.Seed(database, scale));
        timer.Stop();

        var orders = database.Collection("orders");
        var stats = orders.Stats();

        results.Add((
            "tulis / write",
            "bulk insert",
            scale.TotalDocuments / timer.Elapsed.TotalSeconds,
            "dok/s",
            $"{scale.TotalDocuments:N0} documents, indexes included"));

        results.Add((
            "tulis / write",
            "ukuran dokumen",
            stats.AverageDocumentBytes,
            "B",
            "mean encoded size"));

        results.Add((
            "tulis / write",
            "memori",
            stats.ReservedBytes / 1024.0 / 1024.0,
            "MiB",
            $"unmanaged, for {stats.DocumentCount:N0} documents"));

        // --- read path --------------------------------------------------------------------
        var lookupIds = orders.Find("units > 0", limit: 1_000).Select(d => d.Id).ToArray();
        results.Add((
            "baca / read",
            "point lookup by id",
            Measure(() =>
            {
                for (var i = 0; i < 50_000; i++)
                {
                    _ = orders.FindById(lookupIds[i % lookupIds.Length]);
                }
            }, 50_000),
            "op/s",
            "hash lookup plus decode"));

        foreach (var (label, filter) in new[]
                 {
                     ("scan: equality on nested path", "address.city = 'Bandung'"),
                     ("scan: two predicates", "status = 'selesai' AND total > 500000"),
                     ("scan: LIKE prefix", "code LIKE 'SO-2025%'"),
                     ("scan: array membership", "lines[].qty > 4"),
                 })
        {
            foreach (var useNative in new[] { true, false })
            {
                if (useNative && !CuteNative.IsAvailable)
                {
                    continue;
                }

                CuteNative.Disabled = !useNative;
                var suffix = useNative ? " (native)" : " (managed)";

                results.Add((
                    "pindai / scan",
                    label + suffix,
                    scale.Orders / Measure(() => orders.CountWhere(filter), 1) * 1,
                    "baris/s",
                    filter));
            }
        }

        CuteNative.Disabled = false;

        // The index the seed created makes this the same question answered a different way, which
        // is the comparison that actually matters when someone asks whether to add one.
        results.Add((
            "indeks / index",
            "seek: address.city = 'Bandung'",
            scale.Orders / Measure(() => orders.CountWhere("address.city = 'Bandung'"), 1),
            "baris/s",
            "equivalent rows examined per second"));

        results.Add((
            "agregasi / aggregate",
            "GROUP BY city, SUM(total)",
            1 / Measure(() => database.Execute(
                "SELECT address.city, COUNT(*) AS n, SUM(total) AS revenue FROM orders GROUP BY address.city"), 1),
            "query/s",
            "full aggregation over every order"));

        Render(console, results);
        return 0;
    }

    /// <summary>Times an action, returning seconds per run after a warm-up.</summary>
    private static double Measure(Action action, int operations)
    {
        action();

        var rounds = operations > 1 ? 1 : 5;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            action();
        }

        timer.Stop();

        var seconds = timer.Elapsed.TotalSeconds / rounds;
        return operations > 1 ? operations / seconds : seconds;
    }

    private static double Measure<T>(Func<T> action, int operations)
        => Measure(() => _ = action(), operations);

    private static void Render(IAnsiConsole console, List<(string Group, string Name, double Value, string Unit, string Note)> results)
    {
        console.WriteLine();

        var table = new Table
        {
            Border = TableBorder.Minimal,
            BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
        };

        table.AddColumn($"[bold {Theme.Gold}]ukuran / measure[/]");
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]hasil[/]").RightAligned());
        table.AddColumn($"[bold {Theme.Gold}]satuan[/]");
        table.AddColumn($"[bold {Theme.Gold}]catatan[/]");

        string? lastGroup = null;
        foreach (var (group, name, value, unit, note) in results)
        {
            if (group != lastGroup)
            {
                if (lastGroup is not null)
                {
                    table.AddEmptyRow();
                }

                table.AddRow($"[bold {Theme.Muted}]{group.EscapeMarkup()}[/]", string.Empty, string.Empty, string.Empty);
                lastGroup = group;
            }

            var formatted = value >= 1_000
                ? value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("N1", System.Globalization.CultureInfo.InvariantCulture);

            table.AddRow(
                $"[{Theme.Ink}]  {name.EscapeMarkup()}[/]",
                $"[{Theme.Pandan}]{formatted}[/]",
                $"[{Theme.Muted}]{unit.EscapeMarkup()}[/]",
                $"[{Theme.Muted}]{note.EscapeMarkup()}[/]");
        }

        console.Write(table);
        console.WriteLine();
        Theme.WriteNote(console, "Numbers vary with hardware. The published figures come from benchmarks/ (BenchmarkDotNet).");
    }
}
