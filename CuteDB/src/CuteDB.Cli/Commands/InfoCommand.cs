using Spectre.Console;

namespace CuteDB.Cli.Commands;

/// <summary>Shows what a database contains and what it costs to keep open.</summary>
internal sealed class InfoCommand : DatabaseCommand<DatabaseSettings>
{
    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, DatabaseSettings settings)
    {
        var stats = database.Stats();

        Theme.WriteHeading(console, "berkas / file");

        var summary = new Grid()
            .AddColumn(new GridColumn { NoWrap = true, Width = 22 })
            .AddColumn();

        void Row(string label, string value) => summary.AddRow(
            $"[{Theme.Muted}]{label}[/]",
            $"[{Theme.Ink}]{value.EscapeMarkup()}[/]");

        Row("path", stats.Path ?? "(in memory)");
        Row("dibuat / created", stats.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
        Row("ukuran / size", Theme.FormatBytes(stats.FileBytes));
        Row("dokumen / documents", $"{stats.DocumentCount:N0}");
        Row("koleksi / collections", $"{stats.CollectionCount:N0}");

        // Amplification is the number that tells you whether to compact: it is the ratio of what
        // the file holds to what it currently means. Anything much above 2 is mostly history.
        if (stats.LiveBytes > 0)
        {
            var amplification = stats.FileAmplification;
            var colour = amplification switch
            {
                > 4 => Theme.Coral,
                > 2 => Theme.Gold,
                _ => Theme.Pandan,
            };

            summary.AddRow(
                $"[{Theme.Muted}]riwayat / history[/]",
                $"[{colour}]{amplification:N1}×[/] [{Theme.Muted}]file to live data" +
                $"{(amplification > 2 ? " — run `cutedb compact`" : string.Empty)}[/]");
        }

        Row("memori aktif / live", Theme.FormatBytes(stats.LiveBytes));
        Row("memori dipesan / reserved", Theme.FormatBytes(stats.ReservedBytes));

        if (stats.DeadBytes > 0)
        {
            Row("belum diklaim / dead", Theme.FormatBytes(stats.DeadBytes));
        }

        if (database.DiscardedBytesOnOpen > 0)
        {
            summary.AddRow(
                $"[{Theme.Muted}]pemulihan / recovery[/]",
                $"[{Theme.Gold}]{Theme.FormatBytes(database.DiscardedBytesOnOpen)} of damaged tail discarded[/]");
        }

        console.Write(summary);
        console.WriteLine();

        if (database.CollectionNames.Count == 0)
        {
            Theme.WriteNote(console, "Belum ada koleksi. / No collections yet. Try `cutedb seed`.");
            return 0;
        }

        Theme.WriteHeading(console, "koleksi / collections");

        var table = new Table
        {
            Border = TableBorder.Minimal,
            BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
        };

        table.AddColumn($"[bold {Theme.Gold}]nama[/]");
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]dokumen[/]").RightAligned());
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]rata-rata[/]").RightAligned());
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]memori[/]").RightAligned());
        table.AddColumn($"[bold {Theme.Gold}]indeks[/]");

        foreach (var name in database.CollectionNames)
        {
            var collection = database.Collection(name);
            var collectionStats = collection.Stats();
            var indexes = collection.Indexes;

            var indexText = indexes.Count == 0
                ? $"[{Theme.Muted}]—[/]"
                : string.Join(
                    $"[{Theme.Muted}], [/]",
                    indexes.Select(i => $"[{Theme.Ink}]{i.Path.EscapeMarkup()}[/]" +
                                        (i.Unique ? $"[{Theme.Gold}] unik[/]" : string.Empty)));

            table.AddRow(
                $"[{Theme.Ink}]{name.EscapeMarkup()}[/]",
                $"[{Theme.Pandan}]{collectionStats.DocumentCount:N0}[/]",
                $"[{Theme.Muted}]{collectionStats.AverageDocumentBytes:N0} B[/]",
                $"[{Theme.Muted}]{Theme.FormatBytes(collectionStats.LiveBytes)}[/]",
                indexText);
        }

        console.Write(table);
        console.WriteLine();

        Theme.WriteNote(console, $"scanner: {CuteDB.Native.CuteNative.Describe()}");
        return 0;
    }
}
