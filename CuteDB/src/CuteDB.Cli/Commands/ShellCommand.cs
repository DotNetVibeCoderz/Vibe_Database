using System.ComponentModel;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>
/// An interactive CuteQL session.
/// </summary>
/// <remarks>
/// <para>
/// A statement runs when it ends in a semicolon or when a blank line closes it, so multi-line
/// queries can be pasted or typed without escaping anything. Meta-commands start with a backslash,
/// the convention <c>psql</c> established, which keeps them from ever colliding with CuteQL.
/// </para>
/// <para>
/// The session holds one open database for its lifetime. Writes are flushed on exit; a session
/// killed with Ctrl-C loses at most whatever was buffered, and the append-only log recovers the
/// rest on the next open.
/// </para>
/// </remarks>
internal sealed class ShellCommand : DatabaseCommand<ShellCommand.Settings>
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandOption("-n|--max-rows <COUNT>")]
        [Description("Rows to print per result. Default 30.")]
        public int MaxRows { get; init; } = 30;
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        Theme.WriteBanner(console);
        console.MarkupLine(
            $"[{Theme.Muted}]Terhubung ke[/] [{Theme.Ink}]{(database.FilePath ?? "memory").EscapeMarkup()}[/] " +
            $"[{Theme.Muted}]· ketik[/] [{Theme.Gold}]\\?[/] [{Theme.Muted}]untuk bantuan,[/] " +
            $"[{Theme.Gold}]\\q[/] [{Theme.Muted}]untuk keluar[/]");
        console.WriteLine();

        var format = OutputFormat.Table;
        var buffer = new List<string>();

        while (true)
        {
            var continuation = buffer.Count > 0;
            var prompt = continuation
                ? $"[{Theme.Muted}]   …[/] "
                : $"[bold {Theme.Gold}]cutedb[/][{Theme.Muted}]›[/] ";

            console.Markup(prompt);
            var line = Console.ReadLine();

            // End of input — a piped script running out, or Ctrl-D. Treat it as \q.
            if (line is null)
            {
                console.WriteLine();
                break;
            }

            if (!continuation && line.TrimStart().StartsWith('\\'))
            {
                if (!HandleMeta(console, database, line.Trim(), ref format))
                {
                    break;
                }

                continue;
            }

            // A blank line on an empty buffer is just a blank line; on a partial statement it
            // means "run what I have".
            if (line.Trim().Length == 0)
            {
                if (buffer.Count == 0)
                {
                    continue;
                }
            }
            else
            {
                buffer.Add(line);
                if (!line.TrimEnd().EndsWith(';'))
                {
                    continue;
                }
            }

            var statement = string.Join('\n', buffer).TrimEnd().TrimEnd(';');
            buffer.Clear();

            if (statement.Trim().Length == 0)
            {
                continue;
            }

            RunStatement(console, database, statement, format, settings.MaxRows);
        }

        database.Flush(durable: true);
        Theme.WriteNote(console, "Sampai jumpa! / Goodbye.");
        return 0;
    }

    private static void RunStatement(
        IAnsiConsole console,
        CuteDatabase database,
        string statement,
        OutputFormat format,
        int maxRows)
    {
        try
        {
            var result = database.Execute(statement);
            QueryCommand.Render(console, result, format, maxRows);
        }
        catch (Exception error) when (error is CuteDbException or InvalidOperationException)
        {
            Theme.WriteError(console, error);
        }

        console.WriteLine();
    }

    /// <summary>Handles a backslash command. Returns false to end the session.</summary>
    private static bool HandleMeta(IAnsiConsole console, CuteDatabase database, string line, ref OutputFormat format)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "\\q":
            case "\\quit":
            case "\\exit":
                return false;

            case "\\?":
            case "\\h":
            case "\\help":
                WriteHelp(console);
                return true;

            case "\\d":
            case "\\dt":
                WriteCollections(console, database);
                return true;

            case "\\di":
                WriteIndexes(console, database, parts.Length > 1 ? parts[1] : null);
                return true;

            case "\\f":
            case "\\format":
                if (parts.Length < 2)
                {
                    Theme.WriteNote(console, $"Format saat ini: {format.ToString().ToLowerInvariant()}");
                    return true;
                }

                try
                {
                    format = QueryCommand.ParseFormat(parts[1]);
                    Theme.WriteSuccess(console, $"Format: {format.ToString().ToLowerInvariant()}");
                }
                catch (CuteDbException error)
                {
                    Theme.WriteError(console, error);
                }

                return true;

            case "\\i":
            case "\\info":
                WriteStats(console, database);
                return true;

            case "\\e":
            case "\\explain":
                if (parts.Length < 2)
                {
                    Theme.WriteNote(console, "Gunakan: \\explain SELECT ...");
                    return true;
                }

                try
                {
                    var plan = database.Explain(line[(line.IndexOf(' ') + 1)..]);
                    console.MarkupLine($"[{Theme.Ink}]{plan.ToString().EscapeMarkup()}[/]");
                }
                catch (CuteDbException error)
                {
                    Theme.WriteError(console, error);
                }

                return true;

            case "\\compact":
                var reclaimed = database.Compact();
                Theme.WriteSuccess(console, $"Menghemat {Theme.FormatBytes(reclaimed)}.");
                return true;

            case "\\clear":
                console.Clear();
                return true;

            default:
                Theme.WriteNote(console, $"Perintah '{command}' tidak dikenal. Ketik \\? untuk daftar.");
                return true;
        }
    }

    private static void WriteHelp(IAnsiConsole console)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn { NoWrap = true, Width = 18 })
            .AddColumn();

        void Row(string command, string description)
            => grid.AddRow($"[{Theme.Gold}]{command}[/]", $"[{Theme.Muted}]{description}[/]");

        Row("\\?", "this list");
        Row("\\d", "list collections");
        Row("\\di [coll]", "list indexes");
        Row("\\i", "database statistics");
        Row("\\e <query>", "explain how a query would run");
        Row("\\f <format>", "output format: table, json, jsonl, csv");
        Row("\\compact", "reclaim space in the file");
        Row("\\clear", "clear the screen");
        Row("\\q", "quit");

        console.Write(grid);
        console.WriteLine();
        Theme.WriteNote(console, "Statements end with ';' or a blank line. Multi-line input is fine.");
    }

    private static void WriteCollections(IAnsiConsole console, CuteDatabase database)
    {
        if (database.CollectionNames.Count == 0)
        {
            Theme.WriteNote(console, "Belum ada koleksi. / No collections yet.");
            return;
        }

        var table = new Table
        {
            Border = TableBorder.Minimal,
            BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
        };

        table.AddColumn($"[bold {Theme.Gold}]koleksi[/]");
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]dokumen[/]").RightAligned());
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]memori[/]").RightAligned());

        foreach (var name in database.CollectionNames)
        {
            var stats = database.Collection(name).Stats();
            table.AddRow(
                $"[{Theme.Ink}]{name.EscapeMarkup()}[/]",
                $"[{Theme.Pandan}]{stats.DocumentCount:N0}[/]",
                $"[{Theme.Muted}]{Theme.FormatBytes(stats.LiveBytes)}[/]");
        }

        console.Write(table);
    }

    private static void WriteIndexes(IAnsiConsole console, CuteDatabase database, string? collectionName)
    {
        var names = collectionName is null ? database.CollectionNames : [collectionName];
        var any = false;

        foreach (var name in names)
        {
            var collection = database.TryGetCollection(name);
            if (collection is null)
            {
                Theme.WriteNote(console, $"Koleksi '{name}' tidak ada.");
                continue;
            }

            foreach (var index in collection.Indexes)
            {
                any = true;
                console.MarkupLine(
                    $"[{Theme.Muted}]{name.EscapeMarkup()}[/] " +
                    $"[{Theme.Gold}]{index.Name.EscapeMarkup()}[/] " +
                    $"[{Theme.Ink}]{index.Path.EscapeMarkup()}[/] " +
                    $"[{Theme.Muted}]{index.KeyCount:N0} keys, {index.EntryCount:N0} entries" +
                    $"{(index.Unique ? ", unique" : string.Empty)}[/]");
            }
        }

        if (!any)
        {
            Theme.WriteNote(console, "Tidak ada indeks. / No indexes.");
        }
    }

    private static void WriteStats(IAnsiConsole console, CuteDatabase database)
    {
        var stats = database.Stats();
        console.MarkupLine(
            $"[{Theme.Muted}]dokumen[/] [{Theme.Pandan}]{stats.DocumentCount:N0}[/] " +
            $"[{Theme.Muted}]· berkas[/] [{Theme.Ink}]{Theme.FormatBytes(stats.FileBytes)}[/] " +
            $"[{Theme.Muted}]· memori[/] [{Theme.Ink}]{Theme.FormatBytes(stats.LiveBytes)}[/] " +
            $"[{Theme.Muted}]· riwayat[/] [{Theme.Ink}]{stats.FileAmplification:N1}×[/]");
    }
}
