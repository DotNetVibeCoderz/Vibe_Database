using System.Buffers;
using System.IO.Pipelines;
using MemSharp.Protocol;

namespace MemSharp.Persistence;

/// <summary>
/// Records every mutating command to a file so writes since the last snapshot survive a crash.
/// </summary>
/// <remarks>
/// <para>
/// Commands are stored in RESP request form - the same bytes a client would have sent. That makes
/// the log replayable through the ordinary <see cref="Commands.CommandTable"/> with no second
/// parser, and readable with any RESP tool.
/// </para>
/// <para>
/// Appends land in an in-memory buffer and reach the operating system when it fills or when the
/// fsync policy says so. <see cref="FsyncPolicy.Always"/> forces a disk flush before the call
/// returns, which is the only setting that survives a power cut with no loss;
/// <see cref="FsyncPolicy.EverySecond"/> bounds the loss to roughly one second of writes.
/// </para>
/// </remarks>
internal sealed class AppendOnlyLog : IDisposable
{
    private readonly AppendOnlyOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly ArrayBufferWriter<byte> _buffer;

    private FileStream? _file;
    private FileStream? _rewrite;
    private long _lastFsyncTicks;
    private bool _disposed;

    public AppendOnlyLog(AppendOnlyOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _buffer = new ArrayBufferWriter<byte>(options.BufferSize);
        _lastFsyncTicks = clock.GetUtcNow().UtcTicks;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(options.Path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _file = new FileStream(options.Path, FileMode.Append, FileAccess.Write, FileShare.Read, options.BufferSize);
    }

    /// <summary>Bytes written to the log since it was opened.</summary>
    public long Length
    {
        get { lock (_gate) return _file?.Length ?? 0; }
    }

    /// <summary>Appends one command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="captured">
    /// True if a snapshot in progress has already written the shard this command changed. Such a
    /// command also goes to the rewrite segment, because the snapshot does not contain it.
    /// </param>
    public void Append(string command, string[] arguments, bool captured)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_file is null) return;

            int before = _buffer.WrittenCount;
            RespWriter.WriteCommand(_buffer, command, arguments);
            if (captured && _rewrite is not null) _rewrite.Write(_buffer.WrittenSpan[before..]);

            bool full = _buffer.WrittenCount >= _options.BufferSize;
            bool always = _options.Fsync == FsyncPolicy.Always;
            if (full || always) FlushLocked(fsync: always);
            else if (_options.Fsync == FsyncPolicy.EverySecond && SecondElapsed()) FlushLocked(fsync: true);
        }
    }

    /// <summary>Pushes buffered bytes to the operating system, optionally forcing them to disk.</summary>
    public void Flush(bool fsync = true)
    {
        lock (_gate) FlushLocked(fsync);
    }

    private string RewritePath => _options.Path + ".rewrite";

    /// <summary>
    /// Starts collecting the commands a snapshot about to be written will miss.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot is taken a shard at a time while writes continue, so "truncate the log once the
    /// snapshot is saved" - what 1.0 did - deletes every write that landed on a shard after the
    /// snapshot had already passed it. Those writes are in neither file, and a crash loses them.
    /// </para>
    /// <para>
    /// Instead, while a snapshot runs, each command is still appended to the live log (so a crash
    /// mid-snapshot recovers from the old snapshot plus the whole log), and the commands for shards
    /// the snapshot has already captured are copied to a rewrite segment as well. Once the snapshot
    /// is in place, the segment replaces the log.
    /// </para>
    /// </remarks>
    public void BeginRewrite()
    {
        lock (_gate)
        {
            if (_file is null) return;
            _rewrite?.Dispose();
            _rewrite = new FileStream(RewritePath, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize);
        }
    }

    /// <summary>Makes the rewrite segment the log. Called once the snapshot has been moved into place.</summary>
    public void CompleteRewrite()
    {
        lock (_gate)
        {
            if (_file is null || _rewrite is null) return;

            // Everything still buffered is either in the snapshot or already copied to the segment.
            _buffer.Clear();
            _file.Dispose();
            _file = null;

            _rewrite.Flush(flushToDisk: true);
            _rewrite.Dispose();
            _rewrite = null;

            File.Move(RewritePath, _options.Path, overwrite: true);
            _file = new FileStream(_options.Path, FileMode.Append, FileAccess.Write, FileShare.Read, _options.BufferSize);
        }
    }

    /// <summary>Discards the rewrite segment after a failed snapshot, keeping the live log whole.</summary>
    public void AbandonRewrite()
    {
        lock (_gate)
        {
            if (_rewrite is null) return;
            _rewrite.Dispose();
            _rewrite = null;
            try { File.Delete(RewritePath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Replays a log into a database, returning how many commands were applied.
    /// </summary>
    /// <remarks>
    /// A log can end mid-command if the process died between two writes. That tail is dropped
    /// silently and the file truncated to the last complete command: a partial command is not
    /// corruption, it is the write that was in flight when the power went, and refusing to start
    /// because of it would be worse than losing it.
    /// </remarks>
    public static long Replay(string path, MemDb db)
    {
        if (!File.Exists(path)) return 0;

        long applied = 0;
        long lastGoodPosition = 0;

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            var reader = PipeReader.Create(file);
            while (true)
            {
                ReadResult result = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                var buffer = result.Buffer;
                long consumedTotal = 0;

                while (true)
                {
                    var remaining = buffer.Slice(consumedTotal);
                    if (remaining.IsEmpty) break;

                    bool parsed;
                    string[] command;
                    long consumed;
                    try
                    {
                        parsed = RespReader.TryParseCommand(remaining, out command, out consumed);
                    }
                    catch (MemSharpException)
                    {
                        // Malformed bytes rather than a short read: stop here and keep everything
                        // before them.
                        parsed = false;
                        command = Array.Empty<string>();
                        consumed = 0;
                    }

                    if (!parsed) break;

                    if (command.Length > 0)
                    {
                        db.ApplyLoggedCommand(command);
                        applied++;
                    }
                    consumedTotal += consumed;
                    lastGoodPosition += consumed;
                }

                reader.AdvanceTo(buffer.GetPosition(consumedTotal), buffer.End);
                if (result.IsCompleted && consumedTotal == 0) break;
            }
            reader.Complete();
        }

        // Trim the incomplete tail so the next append starts on a command boundary.
        var info = new FileInfo(path);
        if (lastGoodPosition < info.Length)
        {
            using var trim = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            trim.SetLength(lastGoodPosition);
        }

        return applied;
    }

    private bool SecondElapsed()
    {
        long now = _clock.GetUtcNow().UtcTicks;
        if (now - _lastFsyncTicks < TimeSpan.TicksPerSecond) return false;
        _lastFsyncTicks = now;
        return true;
    }

    private void FlushLocked(bool fsync)
    {
        if (_file is null) return;

        if (_buffer.WrittenCount > 0)
        {
            _file.Write(_buffer.WrittenSpan);
            _buffer.Clear();
        }
        _file.Flush(fsync);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            FlushLocked(fsync: true);
            _file?.Dispose();
            _file = null;
            _rewrite?.Dispose();
            _rewrite = null;
        }
    }
}
