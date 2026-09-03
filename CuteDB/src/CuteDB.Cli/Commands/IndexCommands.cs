using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>Settings for the commands that address one collection.</summary>
internal class CollectionSettings : DatabaseSettings
{
    [CommandArgument(1, "<collection>")]
    [Description("The collection to act on.")]
    public string Collection { get; init; } = string.Empty;
}

/// <summary>Lists the indexes on a collection.</summary>
internal sealed class IndexListCommand : DatabaseCommand<CollectionSettings>
{
    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, CollectionSettings settings)
    {
        var collection = Require(database, settings.Collection);
        var indexes = collection.Indexes;

        if (indexes.Count == 0)
        {
            Theme.WriteNote(console, $"'{settings.Collection}' has no secondary indexes.");
            Theme.WriteNote(console, $"Add one with: cutedb index create {settings.Database} {settings.Collection} <path>");
            return 0;
        }

        var table = new Table
        {
            Border = TableBorder.Minimal,
            BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
        };

        table.AddColumn($"[bold {Theme.Gold}]nama[/]");
        table.AddColumn($"[bold {Theme.Gold}]jalur / path[/]");
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]kunci[/]").RightAligned());
        table.AddColumn(new TableColumn($"[bold {Theme.Gold}]entri[/]").RightAligned());
        table.AddColumn($"[bold {Theme.Gold}]unik[/]");

        foreach (var index in indexes)
        {
            table.AddRow(
                $"[{Theme.Ink}]{index.Name.EscapeMarkup()}[/]",
                $"[{Theme.Ink}]{index.Path.EscapeMarkup()}[/]",
                $"[{Theme.Pandan}]{index.KeyCount:N0}[/]",
                $"[{Theme.Pandan}]{index.EntryCount:N0}[/]",
                index.Unique ? $"[{Theme.Gold}]ya[/]" : $"[{Theme.Muted}]—[/]");
        }

        console.Write(table);

        // Entries exceeding keys means duplicates, which for an array-valued path is expected —
        // one entry per element — and for anything else says how selective the index really is.
        var total = indexes.Sum(i => (long)i.EntryCount);
        var keys = indexes.Sum(i => (long)i.KeyCount);
        if (total > keys)
        {
            Theme.WriteNote(console, $"{total - keys:N0} entries share a key with another document.");
        }

        return 0;
    }

    internal static CuteCollection Require(CuteDatabase database, string name)
        => database.TryGetCollection(name)
            ?? throw new CuteDbException(
                $"There is no collection called '{name}'. " +
                $"Existing: {(database.CollectionNames.Count == 0 ? "none" : string.Join(", ", database.CollectionNames))}.");
}

/// <summary>Creates an index over a document path.</summary>
internal sealed class IndexCreateCommand : DatabaseCommand<IndexCreateCommand.Settings>
{
    internal sealed class Settings : CollectionSettings
    {
        [CommandArgument(2, "<path>")]
        [Description("The document path to index, such as address.city or tags.")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("-n|--name <NAME>")]
        [Description("Index name. Defaults to the path.")]
        public string? Name { get; init; }

        [CommandOption("-u|--unique")]
        [Description("Reject documents whose key duplicates an existing one.")]
        public bool Unique { get; init; }
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var collection = IndexListCommand.Require(database, settings.Collection);
        var timer = Stopwatch.StartNew();

        var info = collection.CreateIndex(settings.Path, settings.Name, settings.Unique);
        timer.Stop();

        Theme.WriteSuccess(
            console,
            $"Indeks '{info.Name}' atas {info.Path} — {info.KeyCount:N0} kunci, {info.EntryCount:N0} entri, " +
            $"{Theme.FormatDuration(timer.Elapsed)}");

        if (info.EntryCount == 0 && collection.Count > 0)
        {
            Theme.WriteNote(
                console,
                $"No document has a value at '{settings.Path}'. The index is valid but will never match — " +
                "check the path against a document with `cutedb query`.");
        }

        return 0;
    }
}

/// <summary>Drops an index.</summary>
internal sealed class IndexDropCommand : DatabaseCommand<IndexDropCommand.Settings>
{
    internal sealed class Settings : CollectionSettings
    {
        [CommandArgument(2, "<name>")]
        [Description("The index name, as shown by `cutedb index list`.")]
        public string Name { get; init; } = string.Empty;
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var collection = IndexListCommand.Require(database, settings.Collection);

        if (!collection.DropIndex(settings.Name))
        {
            var available = collection.Indexes.Count == 0
                ? "it has none"
                : $"it has: {string.Join(", ", collection.Indexes.Select(i => i.Name))}";

            throw new CuteDbException($"'{settings.Collection}' has no index called '{settings.Name}' — {available}.");
        }

        Theme.WriteSuccess(console, $"Indeks '{settings.Name}' dihapus.");
        return 0;
    }
}
