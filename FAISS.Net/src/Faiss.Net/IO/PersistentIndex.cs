using System.Runtime.InteropServices;

namespace Faiss.Net.IO;

/// <summary>
/// Incremental persistence for any index: a snapshot file plus an append-only write-ahead journal.
/// <para>
/// Rewriting a whole index after every add is what makes persistence expensive for an index that
/// grows continuously. Here every mutation — train, add, add with ids, remove, reset — is appended
/// to <c>&lt;path&gt;.journal</c> as one length-prefixed, CRC-checked record <em>before</em> it is
/// applied, so a crash loses at most the operation that was being written, and that one is
/// recognised as torn and discarded rather than replayed half-way. Opening the index loads the
/// snapshot and replays the journal; <see cref="Checkpoint"/> folds the journal into a new snapshot.
/// </para>
/// <para>
/// Snapshot and journal carry a generation number. A checkpoint writes the new snapshot to a
/// temporary file, flushes it, atomically replaces the old one, and only then starts a new journal;
/// if the process dies between those steps, the next open sees a journal older than its snapshot and
/// ignores it instead of applying its operations a second time.
/// </para>
/// <para>
/// The wrapper serializes writers with a reader-writer lock (as <see cref="SynchronizedIndex"/>
/// does), so it is safe to share between threads. Removal by predicate cannot be journaled — a
/// delegate has no on-disk form — so only <see cref="RemoveIds(ReadOnlySpan{long})"/> is supported.
/// </para>
/// </summary>
/// <example>
/// <code>
/// using var index = PersistentIndex.Open("vectors.index", () => new IndexFlatL2(384));
/// index.Add(batch);          // journaled and applied
/// index.Checkpoint();        // snapshot rewritten, journal emptied
/// </code>
/// </example>
public sealed class PersistentIndex : Index, IDisposable
{
    private static readonly byte[] SnapshotMagic = "FNPSNP01"u8.ToArray();
    private static readonly byte[] JournalMagic = "FNJRNL01"u8.ToArray();

    private enum Kind : byte { Train = 1, Add = 2, AddWithIds = 3, Remove = 4, Reset = 5 }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly string _path;
    private FileStream _journal;
    private long _generation;
    private bool _disposed;

    /// <summary>The wrapped index.</summary>
    public Index Inner { get; private set; }

    /// <summary>Path of the snapshot file; the journal is the same path with <c>.journal</c> appended.</summary>
    public string Path => _path;

    /// <summary>
    /// When true (the default), each journal record is flushed to the device before the call
    /// returns, so an acknowledged write survives a power loss. False trades that for speed: records
    /// still reach the OS on every call and survive a process crash, but not necessarily a power cut.
    /// </summary>
    public bool DurableWrites { get; set; } = true;

    /// <summary>When positive, a write that grows the journal beyond this many bytes triggers a checkpoint.</summary>
    public long CheckpointAfterBytes { get; set; }

    /// <summary>Current journal size in bytes.</summary>
    public long JournalBytes => _journal.Length;

    /// <summary>Journal records replayed when the index was opened.</summary>
    public int ReplayedRecords { get; private set; }

    /// <summary>True when opening found a torn or corrupt final record and discarded it.</summary>
    public bool RecoveredFromTornWrite { get; private set; }

    private PersistentIndex(string path, Index inner, FileStream journal, long generation)
        : base(inner.D, inner.MetricType)
    {
        _path = path;
        Inner = inner;
        _journal = journal;
        _generation = generation;
        Sync();
    }

    /// <summary>
    /// Opens (or creates) a persistent index. When <paramref name="path"/> holds no snapshot yet,
    /// <paramref name="create"/> supplies the empty index; otherwise it is not called.
    /// </summary>
    public static PersistentIndex Open(string path, Func<Index> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(create);

        Index inner;
        long generation = 0;
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream);
            if (!reader.ReadBytes(SnapshotMagic.Length).AsSpan().SequenceEqual(SnapshotMagic))
                throw new InvalidDataException($"{path} is not a FAISS.Net persistent-index snapshot.");
            generation = reader.ReadInt64();
            inner = IndexIO.ReadIndex(stream);
        }
        else
        {
            inner = create() ?? throw new InvalidOperationException("The factory returned null.");
            WriteSnapshot(path, inner, generation);
        }

        string journalPath = path + ".journal";
        var journal = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var index = new PersistentIndex(path, inner, journal, generation);
        index.Replay();
        return index;
    }

    // --------------------------------------------------------- Journal replay

    private void Replay()
    {
        _journal.Position = 0;
        if (_journal.Length < JournalMagic.Length + sizeof(long))
        {
            StartJournal();
            return;
        }

        using var reader = new BinaryReader(_journal, System.Text.Encoding.UTF8, leaveOpen: true);
        bool valid = reader.ReadBytes(JournalMagic.Length).AsSpan().SequenceEqual(JournalMagic);
        long journalGeneration = reader.ReadInt64();
        if (!valid || journalGeneration != _generation)
        {
            // Older than the snapshot: a checkpoint completed but crashed before resetting the
            // journal. Its operations are already in the snapshot.
            StartJournal();
            return;
        }

        long goodEnd = _journal.Position;
        while (_journal.Position < _journal.Length)
        {
            if (!TryReadRecord(reader, out var kind, out var payload))
            {
                RecoveredFromTornWrite = true;
                break;
            }
            Apply(kind, payload);
            ReplayedRecords++;
            goodEnd = _journal.Position;
        }

        _journal.SetLength(goodEnd); // drop a torn tail so new records start on a boundary
        _journal.Position = goodEnd;
        Sync();
    }

    private bool TryReadRecord(BinaryReader reader, out Kind kind, out byte[] payload)
    {
        kind = 0;
        payload = [];
        long remaining = _journal.Length - _journal.Position;
        if (remaining < 4 + 1 + 4) return false;
        int length = reader.ReadInt32();
        if (length < 0 || length > remaining - 9) return false;
        kind = (Kind)reader.ReadByte();
        payload = reader.ReadBytes(length);
        uint stored = reader.ReadUInt32();
        return stored == Crc32((byte)kind, payload);
    }

    private void Apply(Kind kind, byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        switch (kind)
        {
            case Kind.Train:
                Inner.Train(ReadFloats(reader));
                break;
            case Kind.Add:
                Inner.Add(ReadFloats(reader));
                break;
            case Kind.AddWithIds:
                var x = ReadFloats(reader);
                Inner.AddWithIds(x, ReadLongs(reader));
                break;
            case Kind.Remove:
                Inner.RemoveIds(ReadLongs(reader));
                break;
            case Kind.Reset:
                Inner.Reset();
                break;
            default:
                throw new InvalidDataException($"Unknown journal record kind {(byte)kind}.");
        }
    }

    private static float[] ReadFloats(BinaryReader reader)
    {
        var values = new float[reader.ReadInt32()];
        reader.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static long[] ReadLongs(BinaryReader reader)
    {
        var values = new long[reader.ReadInt32()];
        reader.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    // ---------------------------------------------------------- Journaling

    private void StartJournal()
    {
        _journal.SetLength(0);
        _journal.Position = 0;
        _journal.Write(JournalMagic);
        _journal.Write(BitConverter.GetBytes(_generation));
        _journal.Flush(flushToDisk: true);
    }

    private void Journal(Kind kind, ReadOnlySpan<float> floats, ReadOnlySpan<long> longs, bool hasFloats, bool hasLongs)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (hasFloats)
            {
                writer.Write(floats.Length);
                writer.Write(MemoryMarshal.AsBytes(floats));
            }
            if (hasLongs)
            {
                writer.Write(longs.Length);
                writer.Write(MemoryMarshal.AsBytes(longs));
            }
        }
        var payload = buffer.ToArray();

        // One contiguous write per record: a crash can tear only the record being written.
        var record = new byte[4 + 1 + payload.Length + 4];
        BitConverter.TryWriteBytes(record.AsSpan(0, 4), payload.Length);
        record[4] = (byte)kind;
        payload.CopyTo(record, 5);
        BitConverter.TryWriteBytes(record.AsSpan(5 + payload.Length, 4), Crc32((byte)kind, payload));
        _journal.Write(record);
        _journal.Flush(flushToDisk: DurableWrites);
    }

    private void MaybeCheckpoint()
    {
        if (CheckpointAfterBytes > 0 && _journal.Length > CheckpointAfterBytes) CheckpointLocked();
    }

    // --------------------------------------------------------------- Writes

    public override void Train(ReadOnlySpan<float> x)
    {
        _lock.EnterWriteLock();
        try
        {
            Journal(Kind.Train, x, default, hasFloats: true, hasLongs: false);
            Inner.Train(x);
            Sync();
            MaybeCheckpoint();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        _lock.EnterWriteLock();
        try
        {
            Journal(Kind.Add, x, default, hasFloats: true, hasLongs: false);
            Inner.Add(x);
            Sync();
            MaybeCheckpoint();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        _lock.EnterWriteLock();
        try
        {
            Journal(Kind.AddWithIds, x, ids, hasFloats: true, hasLongs: true);
            Inner.AddWithIds(x, ids);
            Sync();
            MaybeCheckpoint();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        _lock.EnterWriteLock();
        try
        {
            Journal(Kind.Remove, default, ids, hasFloats: false, hasLongs: true);
            long removed = Inner.RemoveIds(ids);
            Sync();
            MaybeCheckpoint();
            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Not supported: a predicate cannot be written to the journal. Pass the ids instead.</summary>
    public override long RemoveIds(Func<long, bool> predicate) =>
        throw new NotSupportedException("PersistentIndex cannot journal a predicate; call RemoveIds(ids) with explicit ids.");

    public override void Reset()
    {
        _lock.EnterWriteLock();
        try
        {
            Journal(Kind.Reset, default, default, hasFloats: false, hasLongs: false);
            Inner.Reset();
            Sync();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Writes the current state as a new snapshot and empties the journal. Cost is proportional to the
    /// index size; call it when the journal has grown large or before a planned shutdown.
    /// </summary>
    public void Checkpoint()
    {
        _lock.EnterWriteLock();
        try { CheckpointLocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    private void CheckpointLocked()
    {
        long next = _generation + 1;
        WriteSnapshot(_path, Inner, next);
        _generation = next;
        StartJournal();
    }

    /// <summary>Snapshot to a temporary file, flushed, then atomically moved over the old one.</summary>
    private static void WriteSnapshot(string path, Index index, long generation)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            stream.Write(SnapshotMagic);
            stream.Write(BitConverter.GetBytes(generation));
            IndexIO.WriteIndex(index, stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- Reads

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Search(queries, nq, k, distances, labels, null);

    public override void Search(
        ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels, SearchParameters? parameters)
    {
        _lock.EnterReadLock();
        try { Inner.Search(queries, nq, k, distances, labels, parameters); }
        finally { _lock.ExitReadLock(); }
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius) =>
        RangeSearch(queries, radius, parameters: null);

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius, SearchParameters? parameters)
    {
        _lock.EnterReadLock();
        try { return Inner.RangeSearch(queries, radius, parameters: parameters); }
        finally { _lock.ExitReadLock(); }
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        _lock.EnterReadLock();
        try { Inner.Reconstruct(key, output); }
        finally { _lock.ExitReadLock(); }
    }

    public override bool SupportsReconstruct => Inner.SupportsReconstruct;

    public override long MemoryUsage => Inner.MemoryUsage;

    public override string Describe() => $"PersistentIndex({_path}, journal={JournalBytes}B, {Inner.Describe()})";

    private void Sync()
    {
        Ntotal = Inner.Ntotal;
        IsTrained = Inner.IsTrained;
    }

    protected internal override IndexTypeCode TypeCode => Inner.TypeCode;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        _lock.EnterReadLock();
        try { Inner.WriteBody(writer); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Closes the journal. Unflushed state is already in the journal; no checkpoint is taken.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _journal.Dispose();
        _lock.Dispose();
    }

    // ------------------------------------------------------------------ CRC

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (IEEE) over the record kind and payload.</summary>
    private static uint Crc32(byte kind, ReadOnlySpan<byte> payload)
    {
        uint crc = 0xFFFFFFFFu;
        crc = CrcTable[(crc ^ kind) & 0xFF] ^ (crc >> 8);
        foreach (byte b in payload) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
