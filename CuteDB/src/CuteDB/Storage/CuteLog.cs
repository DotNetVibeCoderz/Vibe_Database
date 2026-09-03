using System.Buffers;

namespace CuteDB.Storage;

/// <summary>How hard CuteDB works to make a write survive a crash.</summary>
public enum CuteDurability
{
    /// <summary>
    /// Writes sit in CuteDB's own buffer until it fills or the database is closed. Fastest, and
    /// the right choice for a cache, an import staging area, or anything rebuildable.
    /// </summary>
    Buffered = 0,

    /// <summary>
    /// Every write is pushed to the operating system immediately. Survives the process being
    /// killed; does not survive the machine losing power. This is the default.
    /// </summary>
    Flush = 1,

    /// <summary>
    /// Every write is flushed all the way to the storage device. Survives power loss, and costs
    /// roughly two orders of magnitude more per write than <see cref="Flush"/>.
    /// </summary>
    Fsync = 2,
}

/// <summary>Receives frames while a log is being replayed.</summary>
internal interface ILogVisitor
{
    void OnDefineCollection(ushort collectionId, string name);

    void OnDropCollection(ushort collectionId);

    void OnUpsert(ushort collectionId, CuteId id, ReadOnlySpan<byte> document);

    void OnDelete(ushort collectionId, CuteId id);

    void OnDefineIndex(ushort collectionId, string name, string path, bool unique);

    void OnDropIndex(ushort collectionId, string name);
}

/// <summary>
/// The append-only file behind a database: frames in, frames out, and a compaction that rewrites
/// the whole thing.
/// </summary>
/// <remarks>
/// <para>
/// Every change is one frame appended at the end. Nothing already written is ever modified, which
/// is what makes recovery trivial: replay from the top, and stop at the first frame whose length
/// or checksum does not add up, because that is the one that was being written when the process
/// died. Everything before it is intact by construction.
/// </para>
/// <para>
/// The cost of never modifying anything is that the file grows with history — a document updated
/// a thousand times has a thousand frames. <see cref="Compact"/> is what pays that back, writing a
/// fresh file containing only current state and swapping it in atomically.
/// </para>
/// </remarks>
internal sealed class CuteLog : IDisposable
{
    private const int WriteBufferSize = 1 << 20;

    private readonly string _path;
    private readonly bool _readOnly;
    private readonly byte[] _frameHeader = new byte[CuteFileFormat.FrameHeaderSize];

    private FileStream _stream;
    private long _appendedFrames;

    internal CuteLog(string path, CuteDurability durability, bool readOnly = false)
    {
        _path = System.IO.Path.GetFullPath(path);
        _readOnly = readOnly;
        Durability = durability;

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var exists = File.Exists(_path);
        if (!exists && readOnly)
        {
            throw new FileNotFoundException($"There is no CuteDB database at '{_path}'.", _path);
        }

        _stream = new FileStream(
            _path,
            readOnly ? FileMode.Open : FileMode.OpenOrCreate,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite,
            readOnly ? FileShare.ReadWrite : FileShare.Read,
            WriteBufferSize,
            FileOptions.SequentialScan);

        try
        {
            Span<byte> header = stackalloc byte[CuteFileFormat.HeaderSize];
            if (_stream.Length == 0)
            {
                CreatedUtc = DateTime.UtcNow;
                CuteFileFormat.WriteHeader(header, CreatedUtc);
                _stream.Write(header);
                _stream.Flush();
            }
            else
            {
                // A file too short to hold a header is not read exactly — ReadHeader gives a far
                // better message about it than EndOfStreamException does.
                var read = _stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                CreatedUtc = CuteFileFormat.ReadHeader(header[..read]);
            }
        }
        catch
        {
            // Opening a file that turns out not to be a database must not leave the handle open,
            // or the caller cannot even delete the file it was told about.
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>When the database file was first created.</summary>
    internal DateTime CreatedUtc { get; }

    /// <summary>How hard writes work to survive a crash.</summary>
    internal CuteDurability Durability { get; set; }

    /// <summary>The file's current size in bytes.</summary>
    internal long Length => _stream.Length;

    /// <summary>The path on disk.</summary>
    internal string FilePath => _path;

    /// <summary>Frames appended since the log was opened.</summary>
    internal long AppendedFrames => _appendedFrames;

    /// <summary>
    /// Replays the file from the top, reporting how many bytes of damaged tail were discarded.
    /// </summary>
    internal long Replay(ILogVisitor visitor)
    {
        _stream.Position = CuteFileFormat.HeaderSize;

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var lastGoodEnd = (long)CuteFileFormat.HeaderSize;
        try
        {
            while (true)
            {
                var framePosition = _stream.Position;
                var headerRead = _stream.ReadAtLeast(_frameHeader, CuteFileFormat.FrameHeaderSize, throwOnEndOfStream: false);
                if (headerRead < CuteFileFormat.FrameHeaderSize)
                {
                    break;
                }

                if (!CuteFileFormat.TryReadFrameHeader(_frameHeader, out var opcode, out var collectionId, out var payloadLength, out var crc))
                {
                    break;
                }

                if (payloadLength > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                }

                var payload = buffer.AsSpan(0, payloadLength);
                if (_stream.ReadAtLeast(payload, payloadLength, throwOnEndOfStream: false) < payloadLength)
                {
                    break;
                }

                if (Crc32C.Compute(payload) != crc)
                {
                    // A frame that made it to disk only partly. Everything after it is suspect
                    // too, so replay stops here and the tail is truncated.
                    break;
                }

                Dispatch(visitor, opcode, collectionId, payload);
                lastGoodEnd = framePosition + CuteFileFormat.FrameHeaderSize + payloadLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var discarded = _stream.Length - lastGoodEnd;
        if (discarded > 0 && !_readOnly)
        {
            _stream.SetLength(lastGoodEnd);
            _stream.Flush(flushToDisk: true);
        }

        _stream.Position = _stream.Length;
        return Math.Max(0, discarded);
    }

    /// <summary>Appends one frame.</summary>
    internal void Append(CuteOpcode opcode, ushort collectionId, ReadOnlySpan<byte> payload)
    {
        if (_readOnly)
        {
            throw new CuteDbException("This database was opened read-only.");
        }

        if (payload.Length > CuteFileFormat.MaxPayloadSize)
        {
            throw new CuteDbException(
                $"A single document cannot exceed {CuteFileFormat.MaxPayloadSize / (1024 * 1024)} MiB " +
                $"(this one encodes to {payload.Length} bytes).");
        }

        CuteFileFormat.WriteFrameHeader(_frameHeader, opcode, collectionId, payload);
        _stream.Write(_frameHeader);
        _stream.Write(payload);
        _appendedFrames++;

        switch (Durability)
        {
            case CuteDurability.Flush:
                _stream.Flush();
                break;

            case CuteDurability.Fsync:
                _stream.Flush(flushToDisk: true);
                break;
        }
    }

    /// <summary>Pushes anything buffered out, optionally all the way to the device.</summary>
    internal void Flush(bool durable = false) => _stream.Flush(durable);

    /// <summary>
    /// Rewrites the file with only the frames <paramref name="writeAll"/> produces, then swaps it
    /// in. Returns how many bytes the file shrank by.
    /// </summary>
    /// <remarks>
    /// The new file is built beside the old one and moved into place with
    /// <see cref="File.Move(string, string, bool)"/>, so a crash mid-compaction leaves the original
    /// database untouched and only a stray temporary file to clean up.
    /// </remarks>
    internal long Compact(Action<CuteLog> writeAll)
    {
        if (_readOnly)
        {
            throw new CuteDbException("This database was opened read-only.");
        }

        var before = _stream.Length;
        var temporaryPath = _path + ".compact";
        File.Delete(temporaryPath);

        using (var replacement = new CuteLog(temporaryPath, CuteDurability.Buffered))
        {
            writeAll(replacement);
            replacement.Flush(durable: true);
        }

        _stream.Flush(flushToDisk: true);
        _stream.Dispose();

        File.Move(temporaryPath, _path, overwrite: true);

        _stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
        _stream.Position = _stream.Length;
        return before - _stream.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_readOnly)
        {
            try
            {
                Append(CuteOpcode.Checkpoint, 0, ReadOnlySpan<byte>.Empty);
                _stream.Flush(flushToDisk: true);
            }
            catch (ObjectDisposedException)
            {
                // Already closed by a failed compaction; nothing left to checkpoint.
            }
        }

        _stream.Dispose();
    }

    private static void Dispatch(ILogVisitor visitor, CuteOpcode opcode, ushort collectionId, ReadOnlySpan<byte> payload)
    {
        switch (opcode)
        {
            case CuteOpcode.DefineCollection:
                visitor.OnDefineCollection(collectionId, CuteFileFormat.ReadName(payload, out _));
                break;

            case CuteOpcode.DropCollection:
                visitor.OnDropCollection(collectionId);
                break;

            case CuteOpcode.Upsert:
                if (payload.Length < CuteId.Size)
                {
                    throw new CuteCorruptionException("An upsert frame is too short to hold a document id.");
                }

                visitor.OnUpsert(collectionId, CuteId.Read(payload), payload[CuteId.Size..]);
                break;

            case CuteOpcode.Delete:
                visitor.OnDelete(collectionId, CuteId.Read(payload));
                break;

            case CuteOpcode.DefineIndex:
            {
                var unique = payload[0] != 0;
                var name = CuteFileFormat.ReadName(payload[1..], out var nameLength);
                var path = CuteFileFormat.ReadName(payload[(1 + nameLength)..], out _);
                visitor.OnDefineIndex(collectionId, name, path, unique);
                break;
            }

            case CuteOpcode.DropIndex:
                visitor.OnDropIndex(collectionId, CuteFileFormat.ReadName(payload, out _));
                break;

            case CuteOpcode.Checkpoint:
                break;
        }
    }
}
