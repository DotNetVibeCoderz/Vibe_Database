namespace MemSharp.Persistence;

/// <summary>When a snapshot is written to disk.</summary>
public enum PersistenceMode
{
    /// <summary>Never. The database is purely in memory and nothing is loaded at startup.</summary>
    None,

    /// <summary>Only when the application asks - <c>db.Save()</c>, or the <c>SAVE</c> command.</summary>
    Manual,

    /// <summary>
    /// In the background, whenever <see cref="PersistenceOptions.AutoSaveInterval"/> elapses or
    /// <see cref="PersistenceOptions.AutoSaveAfterChanges"/> writes accumulate, whichever comes
    /// first. Manual saves still work.
    /// </summary>
    Automatic,
}

/// <summary>How often the append-only log is flushed to the operating system and fsynced.</summary>
public enum FsyncPolicy
{
    /// <summary>Never fsync; let the OS decide. Fastest, and loses whatever the page cache held on a power cut.</summary>
    Never,

    /// <summary>Fsync at most once a second. The usual balance, and the default.</summary>
    EverySecond,

    /// <summary>Fsync before acknowledging every write. Durable and roughly an order of magnitude slower.</summary>
    Always,
}

/// <summary>
/// Where and how a <see cref="MemDb"/> is persisted.
/// </summary>
/// <remarks>
/// Two independent mechanisms, and they compose:
///
/// <list type="bullet">
/// <item><description>
/// A <b>snapshot</b> is the whole keyspace serialised into one file. Cheap to load, compact, and
/// as current as the last time it was written. Controlled by <see cref="Mode"/>.
/// </description></item>
/// <item><description>
/// The <b>append-only log</b> records each mutating command as it happens. Turning it on changes
/// the durability story from "as of the last snapshot" to "as of the last fsync", at the cost of a
/// sequential write per mutation. Controlled by <see cref="AppendOnly"/>.
/// </description></item>
/// </list>
///
/// On startup the snapshot is loaded first and the log replayed over it, so the log only ever needs
/// to cover the window since the snapshot was taken - which is why a save truncates it.
/// </remarks>
public sealed class PersistenceOptions
{
    /// <summary>Snapshot file path. Required for anything other than <see cref="PersistenceMode.None"/>.</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>When snapshots are taken. Defaults to <see cref="PersistenceMode.None"/>.</summary>
    public PersistenceMode Mode { get; set; } = PersistenceMode.None;

    /// <summary>
    /// Maximum time between automatic snapshots. <see cref="TimeSpan.Zero"/> disables the timer,
    /// leaving <see cref="AutoSaveAfterChanges"/> as the only trigger.
    /// </summary>
    public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number of writes that triggers an automatic snapshot. 0 disables the counter, leaving
    /// <see cref="AutoSaveInterval"/> as the only trigger.
    /// </summary>
    public int AutoSaveAfterChanges { get; set; } = 10_000;

    /// <summary>Load the snapshot (and replay the log) when the database is constructed.</summary>
    public bool LoadOnStartup { get; set; } = true;

    /// <summary>Take a final snapshot when the database is disposed.</summary>
    public bool SaveOnShutdown { get; set; } = true;

    /// <summary>Append-only log settings, or <c>null</c> to run without one.</summary>
    public AppendOnlyOptions? AppendOnly { get; set; }

    /// <summary>A database that keeps everything in memory and never touches the disk.</summary>
    public static PersistenceOptions InMemoryOnly() => new();

    /// <summary>Snapshots only when asked - the "one-time save" configuration.</summary>
    public static PersistenceOptions ManualSnapshot(string path) =>
        new() { SnapshotPath = path, Mode = PersistenceMode.Manual };

    /// <summary>Snapshots on a timer and after a write threshold.</summary>
    public static PersistenceOptions AutomaticSnapshot(string path, TimeSpan? interval = null, int changes = 10_000) =>
        new()
        {
            SnapshotPath = path,
            Mode = PersistenceMode.Automatic,
            AutoSaveInterval = interval ?? TimeSpan.FromSeconds(60),
            AutoSaveAfterChanges = changes,
        };

    /// <summary>Snapshots plus an append-only log - the most durable configuration.</summary>
    public static PersistenceOptions Durable(string snapshotPath, string? logPath = null) =>
        new()
        {
            SnapshotPath = snapshotPath,
            Mode = PersistenceMode.Automatic,
            AppendOnly = new AppendOnlyOptions
            {
                Path = logPath ?? Path.ChangeExtension(snapshotPath, ".aof"),
                Fsync = FsyncPolicy.EverySecond,
            },
        };

    internal void Validate()
    {
        if (Mode != PersistenceMode.None && string.IsNullOrWhiteSpace(SnapshotPath))
        {
            throw new ArgumentException($"{nameof(SnapshotPath)} is required when {nameof(Mode)} is {Mode}.");
        }
        if (AppendOnly is { } log && string.IsNullOrWhiteSpace(log.Path))
        {
            throw new ArgumentException($"{nameof(AppendOnlyOptions)}.{nameof(AppendOnlyOptions.Path)} is required.");
        }
        if (Mode == PersistenceMode.Automatic && AutoSaveInterval <= TimeSpan.Zero && AutoSaveAfterChanges <= 0)
        {
            throw new ArgumentException(
                $"{nameof(PersistenceMode)}.{nameof(PersistenceMode.Automatic)} needs at least one trigger: " +
                $"set {nameof(AutoSaveInterval)}, {nameof(AutoSaveAfterChanges)}, or both.");
        }
    }
}

/// <summary>Append-only log settings.</summary>
public sealed class AppendOnlyOptions
{
    /// <summary>Log file path.</summary>
    public string Path { get; set; } = "memsharp.aof";

    /// <summary>Durability policy. Defaults to <see cref="FsyncPolicy.EverySecond"/>.</summary>
    public FsyncPolicy Fsync { get; set; } = FsyncPolicy.EverySecond;

    /// <summary>
    /// Buffer size in bytes. Writes accumulate here and go to the OS when it fills or the fsync
    /// policy demands it.
    /// </summary>
    public int BufferSize { get; set; } = 64 * 1024;
}
