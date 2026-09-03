using CuteDB.Native;
using CuteDB.Query;
using CuteDB.Storage;

namespace CuteDB;

/// <summary>Settings for opening a database.</summary>
public sealed record CuteDatabaseOptions
{
    /// <summary>The defaults: durable-to-the-OS writes, read-write, auto-compaction on.</summary>
    public static CuteDatabaseOptions Default { get; } = new();

    /// <summary>Fastest writes, at the cost of losing the tail if the process is killed.</summary>
    public static CuteDatabaseOptions Fast { get; } = new() { Durability = CuteDurability.Buffered };

    /// <summary>Every write pushed to the device before it is acknowledged.</summary>
    public static CuteDatabaseOptions Safest { get; } = new() { Durability = CuteDurability.Fsync };

    /// <summary>How hard writes work to survive a crash.</summary>
    public CuteDurability Durability { get; init; } = CuteDurability.Flush;

    /// <summary>Opens without allowing any modification.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The size of each unmanaged memory slab.</summary>
    public int SlabSize { get; init; } = SlabAllocator.DefaultSlabSize;

    /// <summary>
    /// Reclaims memory automatically once enough of it is dead. Turning this off makes write
    /// latency perfectly predictable at the cost of holding on to freed space.
    /// </summary>
    public bool AutoCompact { get; init; } = true;
}

/// <summary>Totals across a whole database.</summary>
/// <param name="Path">The file on disk, or null for an in-memory database.</param>
/// <param name="CollectionCount">Number of collections.</param>
/// <param name="DocumentCount">Live documents across all collections.</param>
/// <param name="FileBytes">Size of the log file.</param>
/// <param name="LiveBytes">Encoded document bytes currently live.</param>
/// <param name="DeadBytes">Bytes awaiting compaction.</param>
/// <param name="ReservedBytes">Unmanaged memory held.</param>
/// <param name="CreatedUtc">When the database was created.</param>
public readonly record struct CuteDatabaseStats(
    string? Path,
    int CollectionCount,
    int DocumentCount,
    long FileBytes,
    long LiveBytes,
    long DeadBytes,
    long ReservedBytes,
    DateTime CreatedUtc)
{
    /// <summary>
    /// How much of the log file is history rather than current state. A high number means
    /// <see cref="CuteDatabase.Compact"/> would shrink the file substantially.
    /// </summary>
    public double FileAmplification => LiveBytes == 0 ? 0 : (double)FileBytes / LiveBytes;
}

/// <summary>
/// An embedded document database: collections of schemaless JSON documents, held in memory and
/// persisted to a single append-only file.
/// </summary>
/// <example>
/// <code>
/// using var db = CuteDatabase.Open("shop.cute");
/// var orders = db.Collection("orders");
///
/// orders.Insert(CuteDocument.Parse("""
///     { "customer": "Sari", "total": 249000, "items": [{ "sku": "KB-01", "qty": 2 }] }
///     """));
///
/// var result = db.Execute("SELECT customer, total FROM orders WHERE total > 100000 ORDER BY total DESC");
/// </code>
/// </example>
/// <remarks>
/// <para>
/// Everything lives in memory while the database is open; the file is the durable record, replayed
/// on open and appended to on every write. That makes reads as fast as the data structures allow
/// and puts a ceiling on database size at roughly the memory available — a deliberate trade for
/// the embedded case, where "fits in RAM" covers the overwhelming majority of real workloads.
/// </para>
/// <para>
/// Instances are thread-safe. Reads run concurrently; writes serialise against each other and
/// against readers.
/// </para>
/// </remarks>
public sealed class CuteDatabase : IDisposable, ILogVisitor
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly Dictionary<string, CuteCollection> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, CuteCollection> _byId = [];
    private readonly CuteLog? _log;
    private readonly CuteDatabaseOptions _options;

    private ushort _nextCollectionId = 1;
    private bool _disposed;
    private int _writesSinceCompactionCheck;

    private CuteDatabase(string? path, CuteDatabaseOptions options)
    {
        _options = options;
        FilePath = path is null ? null : System.IO.Path.GetFullPath(path);

        if (path is null)
        {
            CreatedUtc = DateTime.UtcNow;
            return;
        }

        _log = new CuteLog(path, options.Durability, options.ReadOnly);
        CreatedUtc = _log.CreatedUtc;
        DiscardedBytesOnOpen = _log.Replay(this);
    }

    /// <summary>The database file, or null when this database is in memory only.</summary>
    public string? FilePath { get; }

    /// <summary>When the database file was first created.</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Bytes of damaged tail discarded when the file was opened. Non-zero means the previous
    /// process was interrupted mid-write; the data before that point is intact.
    /// </summary>
    public long DiscardedBytesOnOpen { get; }

    /// <summary>True when the database was opened read-only.</summary>
    public bool IsReadOnly => _options.ReadOnly;

    /// <summary>The names of every collection, in creation order.</summary>
    public IReadOnlyList<string> CollectionNames
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return [.. _byId.OrderBy(static pair => pair.Key).Select(static pair => pair.Value.Name)];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Opens or creates a database file.</summary>
    public static CuteDatabase Open(string path, CuteDatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new CuteDatabase(path, options ?? CuteDatabaseOptions.Default);
    }

    /// <summary>
    /// Creates a database that exists only for the life of the process, with no file behind it.
    /// </summary>
    /// <remarks>
    /// Useful for tests, for the demo application, and as a fast scratch store. Everything else
    /// behaves identically — the same query engine, the same indexes, the same memory layout.
    /// </remarks>
    public static CuteDatabase CreateInMemory(CuteDatabaseOptions? options = null)
        => new(null, options ?? CuteDatabaseOptions.Default);

    /// <summary>Gets a collection, creating it if it does not exist yet.</summary>
    public CuteCollection Collection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _lock.EnterReadLock();
        try
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                return existing;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        _lock.EnterWriteLock();
        try
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var collection = new CuteCollection(this, _nextCollectionId++, name);
            _byName[name] = collection;
            _byId[collection.Id] = collection;

            if (_log is not null && !_options.ReadOnly)
            {
                var writer = CuteBufferWriter.Rent();
                try
                {
                    CuteFileFormat.WriteName(writer, name);
                    _log.Append(CuteOpcode.DefineCollection, collection.Id, writer.WrittenSpan);
                }
                finally
                {
                    CuteBufferWriter.Return(writer);
                }
            }

            return collection;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Returns a collection only if it already exists.</summary>
    public CuteCollection? TryGetCollection(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return _byName.GetValueOrDefault(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Drops a collection and everything in it. Returns false when it did not exist.</summary>
    public bool DropCollection(string name)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_byName.Remove(name, out var collection))
            {
                return false;
            }

            _byId.Remove(collection.Id);
            AppendDropCollection(collection.Id);
            collection.Dispose();
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Runs a CuteQL statement.</summary>
    /// <example>
    /// <code>
    /// var top = db.Execute(
    ///     "SELECT city, SUM(total) AS revenue FROM orders GROUP BY city ORDER BY revenue DESC LIMIT 5");
    /// </code>
    /// </example>
    public CuteQueryResult Execute(string query, CuteParameters? parameters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var statement = CuteParser.ParseStatement(query);
        return QueryExecutor.Execute(this, statement, parameters);
    }

    /// <summary>Runs a CuteQL statement with parameters given inline.</summary>
    /// <example>
    /// <code>
    /// db.Execute("SELECT * FROM orders WHERE city = @city", ("city", CuteValue.String("Bandung")));
    /// </code>
    /// </example>
    public CuteQueryResult Execute(string query, params ReadOnlySpan<(string Name, CuteValue Value)> parameters)
    {
        var bound = new CuteParameters();
        foreach (var (name, value) in parameters)
        {
            bound.Set(name, value);
        }

        return Execute(query, bound);
    }

    /// <summary>Explains how a filter would be executed, without running it to completion.</summary>
    public CuteQueryPlan Explain(string query, CuteParameters? parameters = null)
    {
        var statement = CuteParser.ParseStatement(query);
        if (statement is not SelectStatement select)
        {
            throw new CuteDbException("Only SELECT statements can be explained.");
        }

        var collection = RequireCollection(select.Collection);
        if (select.Where is null)
        {
            return new CuteQueryPlan("Full collection", null, collection.Count, collection.Count, false);
        }

        return Read(collection, (select.Where, parameters), static (c, state) =>
        {
            QueryPlanner.Execute(c, state.Where, state.parameters, int.MaxValue, out var plan);
            return plan;
        });
    }

    /// <summary>Pushes buffered writes out to the operating system, or all the way to the device.</summary>
    public void Flush(bool durable = false)
    {
        _lock.EnterWriteLock();
        try
        {
            _log?.Flush(durable);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reclaims space: rewrites the log with only current state, and rebuilds the in-memory slabs
    /// without the holes left by updates and deletes.
    /// </summary>
    /// <returns>How many bytes the file shrank by.</returns>
    public long Compact()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            foreach (var collection in _byId.Values)
            {
                collection.CompactStore();
            }

            if (_log is null || _options.ReadOnly)
            {
                return 0;
            }

            return _log.Compact(WriteEverythingTo);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Totals across the whole database.</summary>
    public CuteDatabaseStats Stats()
    {
        _lock.EnterReadLock();
        try
        {
            var documents = 0;
            long live = 0;
            long dead = 0;
            long reserved = 0;

            foreach (var collection in _byId.Values)
            {
                var store = collection.Store;
                documents += store.Count;
                live += store.LiveBytes;
                dead += store.DeadBytes;
                reserved += store.ReservedBytes;
            }

            return new CuteDatabaseStats(
                FilePath,
                _byId.Count,
                documents,
                _log?.Length ?? 0,
                live,
                dead,
                reserved,
                CreatedUtc);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>A one-line description of the engine, for banners and diagnostics.</summary>
    public static string EngineDescription
        => $"CuteDB {typeof(CuteDatabase).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"} " +
           $"· format v{CuteFileFormat.Version} · scanner: {CuteNative.Describe()}";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _lock.EnterWriteLock();
        try
        {
            _log?.Dispose();
            foreach (var collection in _byId.Values)
            {
                collection.Dispose();
            }

            _byId.Clear();
            _byName.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _lock.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Locking helpers used by CuteCollection and the executor
    // ---------------------------------------------------------------------------------------

    internal CuteCollection RequireCollection(string name)
        => TryGetCollection(name)
            ?? throw new CuteDbException(
                $"There is no collection called '{name}'. Existing collections: " +
                $"{(CollectionNames.Count == 0 ? "none" : string.Join(", ", CollectionNames))}.");

    internal TResult Read<TResult>(CuteCollection collection, Func<CuteCollection, TResult> body)
    {
        _lock.EnterReadLock();
        try
        {
            return body(collection);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal TResult Read<TState, TResult>(CuteCollection collection, TState state, Func<CuteCollection, TState, TResult> body)
    {
        _lock.EnterReadLock();
        try
        {
            return body(collection, state);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal TResult Write<TState, TResult>(CuteCollection collection, TState state, Func<CuteCollection, TState, TResult> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReadOnly();

        _lock.EnterWriteLock();
        try
        {
            var result = body(collection, state);
            MaybeCompact(1);
            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies an operation to many items under a single lock, with the log left buffered until
    /// the end.
    /// </summary>
    internal int WriteBatch<TItem>(CuteCollection collection, IEnumerable<TItem> items, Action<CuteCollection, TItem> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReadOnly();

        _lock.EnterWriteLock();
        var previousDurability = _log?.Durability ?? CuteDurability.Buffered;
        try
        {
            // Flushing once at the end rather than once per document is most of what makes a bulk
            // load fast. The window where a crash loses the batch is closed by the flush below.
            if (_log is not null)
            {
                _log.Durability = CuteDurability.Buffered;
            }

            var count = 0;
            foreach (var item in items)
            {
                body(collection, item);
                count++;
            }

            if (_log is not null)
            {
                _log.Durability = previousDurability;
                _log.Flush(previousDurability == CuteDurability.Fsync);
            }

            MaybeCompact(count);
            return count;
        }
        finally
        {
            if (_log is not null)
            {
                _log.Durability = previousDurability;
            }

            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Batch overload for operations that return something per item, such as the id an insert
    /// assigned. The result is discarded; only the count comes back.
    /// </summary>
    /// <remarks>
    /// The cast to <see cref="Action{T1, T2}"/> is load-bearing: without it the lambda binds to
    /// this same overload rather than the Action one, because <c>_ = body(...)</c> is an
    /// expression with a value.
    /// </remarks>
    internal int WriteBatch<TItem, TResult>(CuteCollection collection, IEnumerable<TItem> items, Func<CuteCollection, TItem, TResult> body)
    {
        Action<CuteCollection, TItem> discarding = (c, item) => body(c, item);
        return WriteBatch(collection, items, discarding);
    }

    internal void AppendUpsert(ushort collectionId, CuteId id, ReadOnlySpan<byte> document)
    {
        if (_log is null)
        {
            return;
        }

        var writer = CuteBufferWriter.Rent();
        try
        {
            Span<byte> idBytes = stackalloc byte[CuteId.Size];
            id.Write(idBytes);
            writer.WriteBytes(idBytes);
            writer.WriteBytes(document);
            _log.Append(CuteOpcode.Upsert, collectionId, writer.WrittenSpan);
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    internal void AppendDelete(ushort collectionId, CuteId id)
    {
        if (_log is null)
        {
            return;
        }

        Span<byte> idBytes = stackalloc byte[CuteId.Size];
        id.Write(idBytes);
        _log.Append(CuteOpcode.Delete, collectionId, idBytes);
    }

    internal void AppendDropCollection(ushort collectionId)
        => _log?.Append(CuteOpcode.DropCollection, collectionId, ReadOnlySpan<byte>.Empty);

    internal void AppendDefineIndex(ushort collectionId, string name, string path, bool unique)
    {
        if (_log is null)
        {
            return;
        }

        var writer = CuteBufferWriter.Rent();
        try
        {
            writer.WriteByte(unique ? (byte)1 : (byte)0);
            CuteFileFormat.WriteName(writer, name);
            CuteFileFormat.WriteName(writer, path);
            _log.Append(CuteOpcode.DefineIndex, collectionId, writer.WrittenSpan);
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    internal void AppendDropIndex(ushort collectionId, string name)
    {
        if (_log is null)
        {
            return;
        }

        var writer = CuteBufferWriter.Rent();
        try
        {
            CuteFileFormat.WriteName(writer, name);
            _log.Append(CuteOpcode.DropIndex, collectionId, writer.WrittenSpan);
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Recovery
    // ---------------------------------------------------------------------------------------

    void ILogVisitor.OnDefineCollection(ushort collectionId, string name)
    {
        if (_byId.ContainsKey(collectionId))
        {
            return;
        }

        var collection = new CuteCollection(this, collectionId, name);
        _byId[collectionId] = collection;
        _byName[name] = collection;
        _nextCollectionId = Math.Max(_nextCollectionId, (ushort)(collectionId + 1));
    }

    void ILogVisitor.OnDropCollection(ushort collectionId)
    {
        if (_byId.TryGetValue(collectionId, out var collection))
        {
            // A drop frame is also what Clear() writes, so the collection stays defined and only
            // its contents go.
            collection.ReplayClear();
        }
    }

    void ILogVisitor.OnUpsert(ushort collectionId, CuteId id, ReadOnlySpan<byte> document)
        => Resolve(collectionId)?.ReplayUpsert(id, document);

    void ILogVisitor.OnDelete(ushort collectionId, CuteId id) => Resolve(collectionId)?.ReplayDelete(id);

    void ILogVisitor.OnDefineIndex(ushort collectionId, string name, string path, bool unique)
        => Resolve(collectionId)?.ReplayDefineIndex(name, path, unique);

    void ILogVisitor.OnDropIndex(ushort collectionId, string name) => Resolve(collectionId)?.ReplayDropIndex(name);

    private CuteCollection? Resolve(ushort collectionId) => _byId.GetValueOrDefault(collectionId);

    private void WriteEverythingTo(CuteLog replacement)
    {
        var writer = CuteBufferWriter.Rent();

        // Hoisted above both loops: a stackalloc inside a loop grows the frame on every iteration,
        // which over many collections and many rows is a stack overflow waiting to happen.
        Span<byte> idBytes = stackalloc byte[CuteId.Size];

        try
        {
            foreach (var collection in _byId.Values.OrderBy(static c => c.Id))
            {
                writer.Reset();
                CuteFileFormat.WriteName(writer, collection.Name);
                replacement.Append(CuteOpcode.DefineCollection, collection.Id, writer.WrittenSpan);

                var store = collection.Store;
                var refs = store.Refs;
                for (var row = 0; row < refs.Length; row++)
                {
                    if (refs[row].IsEmpty)
                    {
                        continue;
                    }

                    writer.Reset();
                    store.IdAt(row).Write(idBytes);
                    writer.WriteBytes(idBytes);
                    writer.WriteBytes(store.Read(row));
                    replacement.Append(CuteOpcode.Upsert, collection.Id, writer.WrittenSpan);
                }

                foreach (var index in collection.IndexMap.Values)
                {
                    writer.Reset();
                    writer.WriteByte(index.Unique ? (byte)1 : (byte)0);
                    CuteFileFormat.WriteName(writer, index.Name);
                    CuteFileFormat.WriteName(writer, index.Path.Text);
                    replacement.Append(CuteOpcode.DefineIndex, collection.Id, writer.WrittenSpan);
                }
            }
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    private void MaybeCompact(int writes)
    {
        if (!_options.AutoCompact)
        {
            return;
        }

        // Checking every write would mean summing slab statistics on the hot path; checking every
        // few thousand keeps the amortised cost invisible while still reclaiming memory promptly
        // on a delete-heavy workload.
        _writesSinceCompactionCheck += writes;
        if (_writesSinceCompactionCheck < 4096)
        {
            return;
        }

        _writesSinceCompactionCheck = 0;
        foreach (var collection in _byId.Values)
        {
            collection.CompactStore();
        }
    }

    private void ThrowIfReadOnly()
    {
        if (_options.ReadOnly)
        {
            throw new CuteDbException("This database was opened read-only.");
        }
    }
}
