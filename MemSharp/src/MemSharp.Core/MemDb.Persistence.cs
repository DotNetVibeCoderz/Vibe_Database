using System.Text;
using MemSharp.Collections;
using MemSharp.Persistence;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Writes a snapshot to the configured path, replacing any existing file.</summary>
    /// <remarks>
    /// Synchronous and blocking. The file is written to a temporary path and moved into place, so a
    /// crash mid-write leaves the previous snapshot intact rather than a half-written one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No snapshot path is configured.</exception>
    public void Save()
    {
        var coordinator = _persistence ?? throw new InvalidOperationException(
            "This database has no snapshot path. Set MemDbOptions.Persistence.SnapshotPath to enable saving.");
        coordinator.SaveNow();
    }

    /// <summary>Writes a snapshot on a background thread.</summary>
    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var coordinator = _persistence ?? throw new InvalidOperationException(
            "This database has no snapshot path. Set MemDbOptions.Persistence.SnapshotPath to enable saving.");
        return Task.Run(coordinator.SaveNow, cancellationToken);
    }

    /// <summary>Writes a snapshot to an explicit path, whatever the configured mode.</summary>
    public void SaveTo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        WriteSnapshot(path);
    }

    /// <summary>Replaces the contents of this database with a snapshot from disk.</summary>
    public void LoadFrom(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ReadSnapshot(path);
    }

    /// <summary>Writes on a snapshot pending since the last save.</summary>
    public long PendingChanges => _persistence?.PendingChanges ?? 0;

    /// <summary>UTC time of the last successful snapshot, or <c>null</c> if none has been written.</summary>
    public DateTimeOffset? LastSaveTime => _persistence?.LastSaveTime;

    /// <summary>
    /// Serialises the whole keyspace.
    /// </summary>
    /// <remarks>
    /// Shard by shard, taking one lock at a time. That means the file is not a single point-in-time
    /// image of the database: a write to shard 5 can land after shard 4 was written. The alternative
    /// - holding every shard lock for the length of a multi-hundred-megabyte write - would stop the
    /// database dead for the duration. Per-key consistency is preserved, which is what a key/value
    /// store's snapshot actually needs; if a cross-key atomic image is required, stop writing first.
    /// </remarks>
    internal void WriteSnapshot(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write beside the target, then move into place. A crash before the move leaves the previous
        // snapshot untouched; without this a crash mid-write destroys the only copy.
        string temporary = path + ".tmp";
        long count = 0;
        ulong checksum;

        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Span<byte> header = stackalloc byte[SnapshotFormat.HeaderLength];
            header.Clear();
            file.Write(header);   // placeholder; rewritten once the count and checksum are known

            var hashing = new HashingStream(file);
            using (var writer = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var shard in _shards)
                {
                    long now = NowTicks;
                    lock (shard.Gate)
                    {
                        foreach (var pair in shard.Map)
                        {
                            if (pair.Value.IsExpired(now)) continue;
                            WriteEntry(writer, pair.Key, pair.Value);
                            count++;
                        }
                    }
                }
                writer.Flush();
            }

            checksum = hashing.Hash;

            file.Position = 0;
            using (var headerWriter = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                headerWriter.Write(SnapshotFormat.Magic);
                headerWriter.Write(SnapshotFormat.Version);
                headerWriter.Write(0);            // flags, reserved
                headerWriter.Write(count);
                headerWriter.Write(checksum);
                headerWriter.Flush();
            }

            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteEntry(BinaryWriter writer, string key, in StoreEntry entry)
    {
        writer.Write((byte)entry.Type);
        writer.Write(key);
        writer.Write(entry.ExpiresAtTicks);

        switch (entry.Type)
        {
            case MemType.String:
                writer.Write((string)entry.Value);
                break;

            case MemType.List:
            {
                var list = (Deque<string>)entry.Value;
                writer.Write(list.Count);
                foreach (var item in list.Enumerate()) writer.Write(item);
                break;
            }

            case MemType.Hash:
            {
                var hash = (Dictionary<string, string>)entry.Value;
                writer.Write(hash.Count);
                foreach (var pair in hash)
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value);
                }
                break;
            }

            case MemType.Set:
            {
                var set = (HashSet<string>)entry.Value;
                writer.Write(set.Count);
                foreach (var member in set) writer.Write(member);
                break;
            }

            case MemType.SortedSet:
            {
                var sorted = (SortedSetStore)entry.Value;
                writer.Write(sorted.Count);
                foreach (var member in sorted.All())
                {
                    writer.Write(member.Member);
                    writer.Write(member.Score);
                }
                break;
            }

            case MemType.TimeSeries:
            {
                var series = (TimeSeriesStore)entry.Value;
                var (timestamps, values) = series.Materialise();
                writer.Write(series.Retention);
                writer.Write(timestamps.Length);
                for (int i = 0; i < timestamps.Length; i++)
                {
                    writer.Write(timestamps[i]);
                    writer.Write(values[i]);
                }
                break;
            }

            case MemType.Stream:
            {
                var stream = (StreamStore)entry.Value;
                writer.Write(stream.Count);
                foreach (var streamEntry in stream.All())
                {
                    writer.Write(streamEntry.Id.Milliseconds);
                    writer.Write(streamEntry.Id.Sequence);
                    writer.Write(streamEntry.Fields.Length);
                    foreach (var field in streamEntry.Fields) writer.Write(field);
                }
                break;
            }
        }
    }

    /// <summary>Loads a snapshot, replacing everything currently in the database.</summary>
    internal void ReadSnapshot(string path)
    {
        if (!File.Exists(path)) return;

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new BinaryReader(file, Encoding.UTF8);

        Span<byte> magic = stackalloc byte[SnapshotFormat.Magic.Length];
        if (file.Read(magic) != magic.Length || !magic.SequenceEqual(SnapshotFormat.Magic))
        {
            throw new PersistenceException($"'{path}' is not a MemSharp snapshot.");
        }

        int version = reader.ReadInt32();
        if (version > SnapshotFormat.Version)
        {
            throw new PersistenceException(
                $"'{path}' was written by a newer MemSharp (format {version}; this build reads {SnapshotFormat.Version}).");
        }

        reader.ReadInt32();                         // flags
        long count = reader.ReadInt64();
        ulong expected = reader.ReadUInt64();

        // Verify before installing anything. Loading half a corrupt file and then failing would
        // leave the database in a state that is neither the old contents nor the new.
        long bodyStart = file.Position;
        ulong actual = SnapshotFormat.Seed;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            actual = SnapshotFormat.Hash(buffer.AsSpan(0, read), actual);
        }
        if (actual != expected)
        {
            throw new PersistenceException(
                $"'{path}' failed its checksum: the file is truncated or corrupt.");
        }

        file.Position = bodyStart;
        Clear();

        for (long i = 0; i < count; i++)
        {
            var type = (MemType)reader.ReadByte();
            string key = reader.ReadString();
            long expiry = reader.ReadInt64();

            object value = ReadPayload(reader, type);
            var shard = ShardFor(key);
            lock (shard.Gate)
            {
                shard.Map[key] = new StoreEntry(type, value, expiry);
                if (expiry != 0) shard.VolatileCount++;
            }
        }
    }

    private static object ReadPayload(BinaryReader reader, MemType type)
    {
        switch (type)
        {
            case MemType.String:
                return reader.ReadString();

            case MemType.List:
            {
                int count = reader.ReadInt32();
                var list = new Deque<string>(Math.Max(4, count));
                for (int i = 0; i < count; i++) list.PushBack(reader.ReadString());
                return list;
            }

            case MemType.Hash:
            {
                int count = reader.ReadInt32();
                var hash = new Dictionary<string, string>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++) hash[reader.ReadString()] = reader.ReadString();
                return hash;
            }

            case MemType.Set:
            {
                int count = reader.ReadInt32();
                var set = new HashSet<string>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++) set.Add(reader.ReadString());
                return set;
            }

            case MemType.SortedSet:
            {
                int count = reader.ReadInt32();
                var sorted = new SortedSetStore();
                for (int i = 0; i < count; i++)
                {
                    string member = reader.ReadString();
                    sorted.Add(member, reader.ReadDouble());
                }
                return sorted;
            }

            case MemType.TimeSeries:
            {
                int retention = reader.ReadInt32();
                int count = reader.ReadInt32();
                var series = new TimeSeriesStore(retention, Math.Max(4, count));
                for (int i = 0; i < count; i++) series.Append(reader.ReadInt64(), reader.ReadDouble());
                return series;
            }

            case MemType.Stream:
            {
                int count = reader.ReadInt32();
                var stream = new StreamStore();
                for (int i = 0; i < count; i++)
                {
                    var id = new StreamId(reader.ReadInt64(), reader.ReadInt64());
                    int fieldCount = reader.ReadInt32();
                    var fields = new string[fieldCount];
                    for (int f = 0; f < fieldCount; f++) fields[f] = reader.ReadString();
                    stream.AppendRaw(new StreamEntry(id, fields));
                }
                return stream;
            }

            default:
                throw new PersistenceException($"snapshot contains an unknown value kind ({(byte)type}).");
        }
    }

    /// <summary>Applies one logged command during append-only replay.</summary>
    internal void ApplyLoggedCommand(string[] arguments)
    {
        var reply = Commands.CommandTable.Execute(new Commands.CommandContext(this, null), arguments);
        if (reply.Kind == Protocol.RespKind.Error)
        {
            throw new PersistenceException($"replaying '{arguments[0]}' from the append-only log failed: {reply.Text}");
        }
    }
}
