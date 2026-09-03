using MemSharp.Persistence;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace MemSharp.Cli;

/// <summary>
/// Persistence options shared by every command that opens a database.
/// </summary>
/// <remarks>
/// Declared once and inherited, so <c>--data</c> and <c>--sync</c> mean the same thing whether you
/// are running the REPL, hosting a server or browsing a keyspace - and so a snapshot written by one
/// command is opened by the next without the user restating how.
/// </remarks>
internal class DatabaseSettings : CommandSettings
{
    [CommandOption("-d|--data <PATH>")]
    [Description("Snapshot file. Loaded at startup if it exists. Without it the database is memory-only.")]
    public string? DataFile { get; init; }

    [CommandOption("-s|--sync <MODE>")]
    [Description("When to save: none, manual (SAVE only) or auto. Default: manual when --data is given.")]
    public string? SyncMode { get; init; }

    [CommandOption("--sync-interval <SECONDS>")]
    [Description("Seconds between automatic saves. Default 60.")]
    public int SyncInterval { get; init; } = 60;

    [CommandOption("--sync-changes <COUNT>")]
    [Description("Writes that trigger an automatic save. Default 10000.")]
    public int SyncChanges { get; init; } = 10_000;

    [CommandOption("--aof")]
    [Description("Also keep an append-only log beside the snapshot, for crash durability.")]
    public bool AppendOnly { get; init; }

    [CommandOption("--fsync <POLICY>")]
    [Description("Append-only durability: never, second or always. Default second.")]
    public string? Fsync { get; init; }

    [CommandOption("--shards <COUNT>")]
    [Description("Keyspace shards. Default: four per processor.")]
    public int Shards { get; init; }

    public override Spectre.Console.ValidationResult Validate()
    {
        if (SyncMode is { } mode && !TryParseMode(mode, out _))
        {
            return Spectre.Console.ValidationResult.Error($"--sync must be none, manual or auto (got '{mode}').");
        }
        if (Fsync is { } policy && !TryParseFsync(policy, out _))
        {
            return Spectre.Console.ValidationResult.Error($"--fsync must be never, second or always (got '{policy}').");
        }
        if (SyncMode is not null && DataFile is null)
        {
            return Spectre.Console.ValidationResult.Error("--sync needs --data: there is nowhere to save to.");
        }
        if (AppendOnly && DataFile is null)
        {
            return Spectre.Console.ValidationResult.Error("--aof needs --data: the log lives beside the snapshot.");
        }
        return Spectre.Console.ValidationResult.Success();
    }

    internal static bool TryParseMode(string text, out PersistenceMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "none" or "off": mode = PersistenceMode.None; return true;
            case "manual" or "once" or "one-time": mode = PersistenceMode.Manual; return true;
            case "auto" or "automatic": mode = PersistenceMode.Automatic; return true;
            default: mode = PersistenceMode.None; return false;
        }
    }

    internal static bool TryParseFsync(string text, out FsyncPolicy policy)
    {
        switch (text.ToLowerInvariant())
        {
            case "never" or "no": policy = FsyncPolicy.Never; return true;
            case "second" or "everysecond" or "1s": policy = FsyncPolicy.EverySecond; return true;
            case "always" or "all": policy = FsyncPolicy.Always; return true;
            default: policy = FsyncPolicy.EverySecond; return false;
        }
    }
}

internal static class DatabaseFactory
{
    /// <summary>Builds a database from the shared settings, reporting what it did.</summary>
    public static MemDb Open(DatabaseSettings settings, bool announce = true)
    {
        var persistence = new PersistenceOptions();

        if (settings.DataFile is { } path)
        {
            persistence.SnapshotPath = Path.GetFullPath(path);

            // --data alone means manual: the file is loaded and saved on exit, but nothing is
            // written behind the user's back. Automatic saving is opt-in.
            persistence.Mode = settings.SyncMode is { } mode && DatabaseSettings.TryParseMode(mode, out var parsed)
                ? parsed
                : PersistenceMode.Manual;

            persistence.AutoSaveInterval = TimeSpan.FromSeconds(Math.Max(1, settings.SyncInterval));
            persistence.AutoSaveAfterChanges = Math.Max(0, settings.SyncChanges);

            if (settings.AppendOnly)
            {
                DatabaseSettings.TryParseFsync(settings.Fsync ?? "second", out var fsync);
                persistence.AppendOnly = new AppendOnlyOptions
                {
                    Path = Path.ChangeExtension(persistence.SnapshotPath, ".aof"),
                    Fsync = fsync,
                };
            }
        }

        var db = new MemDb(new MemDbOptions
        {
            ShardCount = settings.Shards,
            Persistence = persistence,
        });

        if (announce) Announce(db, persistence);
        return db;
    }

    private static void Announce(MemDb db, PersistenceOptions persistence)
    {
        if (persistence.SnapshotPath is null)
        {
            AnsiConsole.MarkupLine($"[{Theme.Muted}]memory only - nothing will be written to disk[/]");
            return;
        }

        string state = File.Exists(persistence.SnapshotPath)
            ? $"loaded [{Theme.Value}]{Theme.Count(db.Count)}[/] keys"
            : "new database";

        string schedule = persistence.Mode switch
        {
            PersistenceMode.Automatic =>
                $"auto-save every [{Theme.Value}]{persistence.AutoSaveInterval.TotalSeconds:N0}s[/] " +
                $"or [{Theme.Value}]{Theme.Count(persistence.AutoSaveAfterChanges)}[/] writes",
            PersistenceMode.Manual => "manual save (SAVE, or .save in the REPL)",
            _ => "not saving",
        };

        AnsiConsole.MarkupLine(
            $"[{Theme.Muted}]{Theme.Safe(persistence.SnapshotPath)}[/]  [{Theme.Muted}]-[/]  {state}[{Theme.Muted}],[/] {schedule}");

        if (persistence.AppendOnly is { } log)
        {
            AnsiConsole.MarkupLine(
                $"[{Theme.Muted}]append-only log at[/] [{Theme.Muted}]{Theme.Safe(log.Path)}[/] " +
                $"[{Theme.Muted}]fsync[/] [{Theme.Value}]{log.Fsync.ToString().ToLowerInvariant()}[/]");
        }
    }
}
