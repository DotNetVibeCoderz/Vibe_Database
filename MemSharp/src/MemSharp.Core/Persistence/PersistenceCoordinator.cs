namespace MemSharp.Persistence;

/// <summary>
/// Owns everything durable about a database: the snapshot timer, the change counter and the
/// append-only log.
/// </summary>
/// <remarks>
/// Kept out of <see cref="MemDb"/> so the engine's hot path holds one nullable field and a null
/// check, rather than the timer, path and counter state of a feature most in-memory databases never
/// switch on.
/// </remarks>
internal sealed class PersistenceCoordinator : IDisposable
{
    private readonly MemDb _db;
    private readonly PersistenceOptions _options;
    private readonly AppendOnlyLog? _log;
    private readonly ITimer? _timer;
    private readonly Lock _saveGate = new();


    private long _pendingChanges;
    private long _lastSaveTicks;
    private volatile bool _disposed;

    /// <summary>Builds a coordinator, or returns null when nothing needs to be persisted.</summary>
    public static PersistenceCoordinator? Create(MemDb db, PersistenceOptions options, TimeProvider clock)
    {
        if (options.Mode == PersistenceMode.None && options.AppendOnly is null) return null;
        return new PersistenceCoordinator(db, options, clock);
    }

    private PersistenceCoordinator(MemDb db, PersistenceOptions options, TimeProvider clock)
    {
        _db = db;
        _options = options;

        // Restore before opening the log for append. Replay needs exclusive-enough access to trim an
        // incomplete tail, and a log opened first would re-record every command replay applies.
        if (options.LoadOnStartup) Restore();

        if (options.AppendOnly is { } logOptions) _log = new AppendOnlyLog(logOptions, clock);

        if (options.Mode == PersistenceMode.Automatic && options.AutoSaveInterval > TimeSpan.Zero)
        {
            _timer = clock.CreateTimer(
                static state => ((PersistenceCoordinator)state!).OnTimer(),
                this,
                options.AutoSaveInterval,
                options.AutoSaveInterval);
        }
    }

    public long PendingChanges => Interlocked.Read(ref _pendingChanges);

    public DateTimeOffset? LastSaveTime
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastSaveTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Notes a mutation: counts it and, if a log is configured, records it.</summary>
    /// <param name="command">The command, as it should replay.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="captured">
    /// True if the mutated shard was already written by a snapshot in progress, so the write must
    /// outlive that snapshot's log truncation.
    /// </param>
    public void RecordWrite(string command, string[] arguments, bool captured)
    {
        if (_disposed) return;

        _log?.Append(command, arguments, captured);

        long pending = Interlocked.Increment(ref _pendingChanges);

        if (_options.Mode == PersistenceMode.Automatic &&
            _options.AutoSaveAfterChanges > 0 &&
            pending >= _options.AutoSaveAfterChanges)
        {
            // Reset before dispatching, so a burst of writes cannot queue a second background save
            // behind the first one.
            Interlocked.Exchange(ref _pendingChanges, 0);
            _ = Task.Run(SaveQuietly);
        }
    }

    /// <summary>Writes a snapshot now, blocking until it is on disk.</summary>
    public void SaveNow()
    {
        if (_options.SnapshotPath is not { } path)
        {
            throw new InvalidOperationException("No snapshot path is configured.");
        }

        // One save at a time. Two concurrent writers would race on the same temporary file and the
        // move that follows it.
        lock (_saveGate)
        {
            // Reset before the rewrite starts, not after: a stale flag from the previous save would
            // send a write the new snapshot is about to include into the rewritten log as well, and
            // it would replay twice.
            _db.ResetCaptured();
            _log?.BeginRewrite();

            try
            {
                _db.WriteSnapshot(path, markCaptured: true);
            }
            catch
            {
                _log?.AbandonRewrite();
                throw;
            }

            Interlocked.Exchange(ref _pendingChanges, 0);
            Interlocked.Exchange(ref _lastSaveTicks, DateTimeOffset.UtcNow.UtcTicks);

            // The snapshot is in place, so the log becomes just the writes it missed. Swapping only
            // after the move means a crash at any point leaves either the old snapshot with the
            // whole old log, or the new snapshot with the writes it does not contain.
            _log?.CompleteRewrite();
        }
    }

    private void OnTimer()
    {
        if (_disposed) return;
        if (Interlocked.Read(ref _pendingChanges) == 0) return;   // nothing changed; don't rewrite the file
        SaveQuietly();
    }

    /// <summary>
    /// Saves, swallowing I/O failures.
    /// </summary>
    /// <remarks>
    /// A background save that throws would take down a timer callback or a thread-pool thread, which
    /// on .NET terminates the process. Losing a snapshot is recoverable; killing the host process
    /// because a disk was briefly full is not. Foreground <see cref="SaveNow"/> still propagates,
    /// so a caller who asked for a save is told when it failed.
    /// </remarks>
    private void SaveQuietly()
    {
        try
        {
            SaveNow();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loads the snapshot, then replays the log over it.
    /// </summary>
    /// <remarks>
    /// Order matters: the snapshot is the base image and the log holds only what happened after it
    /// was taken, so replaying first and loading second would throw those writes away.
    /// </remarks>
    private void Restore()
    {
        if (_options.SnapshotPath is { } path && File.Exists(path)) _db.ReadSnapshot(path);

        if (_options.AppendOnly is { } logOptions && File.Exists(logOptions.Path))
        {
            AppendOnlyLog.Replay(logOptions.Path, _db);
        }

        Interlocked.Exchange(ref _pendingChanges, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();

        if (_options.SaveOnShutdown && _options.Mode != PersistenceMode.None && _options.SnapshotPath is not null)
        {
            SaveQuietly();
        }

        _log?.Dispose();
    }
}
