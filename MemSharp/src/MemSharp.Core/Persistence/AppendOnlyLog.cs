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
    public void Append(string command, string[] arguments)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_file is null) return;

            RespWriter.WriteCommand(_buffer, command, arguments);

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

    /// <summary>
    /// Empties the log. Called right after a snapshot, because the snapshot already contains
    /// everything the log was covering.
    /// </summary>
    public void Truncate()
    {
        lock (_gate)
        {
            if (_file is null) return;

            _buffer.Clear();
            _file.Flush(true);
            _file.Dispose();

            // FileMode.Create rather than SetLength(0): the handle was opened for append, and on
            // some platforms an append handle's position does not reliably follow a truncation.
            _file = new FileStream(_options.Path, FileMode.Create, FileAccess.Write, FileShare.Read, _options.BufferSize);
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
        }
    }
}
