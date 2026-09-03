using CuteDB.Indexing;
using CuteDB.Query;
using CuteDB.Storage;

namespace CuteDB;

/// <summary>Counts describing a collection's size and memory use.</summary>
/// <param name="Name">The collection name.</param>
/// <param name="DocumentCount">Live documents.</param>
/// <param name="IndexCount">Secondary indexes defined.</param>
/// <param name="LiveBytes">Bytes of encoded documents currently live.</param>
/// <param name="DeadBytes">Bytes freed by updates and deletes, awaiting compaction.</param>
/// <param name="ReservedBytes">Unmanaged memory held for this collection.</param>
public readonly record struct CuteCollectionStats(
    string Name,
    int DocumentCount,
    int IndexCount,
    long LiveBytes,
    long DeadBytes,
    long ReservedBytes)
{
    /// <summary>Mean encoded document size, or zero for an empty collection.</summary>
    public double AverageDocumentBytes => DocumentCount == 0 ? 0 : (double)LiveBytes / DocumentCount;
}

/// <summary>
/// One named collection of documents.
/// </summary>
/// <remarks>
/// <para>
/// A collection imposes no schema. Two documents in the same collection may share no fields at
/// all, and a field may hold a different type in every document — which is exactly the case
/// CuteQL's comparison rules and the sparse-index behaviour are built around.
/// </para>
/// <para>
/// Every method here is safe to call from multiple threads. Reads take a shared lock and writes
/// an exclusive one, both held on the owning <see cref="CuteDatabase"/>, so writes to different
/// collections in the same database still serialise against each other. That is a deliberate
/// simplification: the log they all append to is a single file, so finer-grained locking would
/// not buy concurrency where it matters.
/// </para>
/// </remarks>
public sealed class CuteCollection
{
    private readonly CuteDatabase _database;
    private readonly DocumentStore _store;
    private readonly Dictionary<string, SecondaryIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CuteValue> _keyScratch = new(4);

    internal CuteCollection(CuteDatabase database, ushort id, string name)
    {
        _database = database;
        Id = id;
        Name = name;
        _store = new DocumentStore();
    }

    /// <summary>The collection's name.</summary>
    public string Name { get; }

    /// <summary>The number of live documents.</summary>
    public int Count => _database.Read(this, static c => c._store.Count);

    /// <summary>The secondary indexes defined on this collection.</summary>
    public IReadOnlyList<CuteIndexInfo> Indexes
        => _database.Read(this, static c => (IReadOnlyList<CuteIndexInfo>)[.. c._indexes.Values.Select(i => i.Info)]);

    internal ushort Id { get; }

    internal DocumentStore Store => _store;

    internal IReadOnlyDictionary<string, SecondaryIndex> IndexMap => _indexes;

    // ---------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------

    /// <summary>Inserts a document, assigning an id if it has none, and returns that id.</summary>
    public CuteId Insert(CuteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _database.Write(this, document, static (c, d) => c.UpsertCore(d, requireNew: true));
    }

    /// <summary>Inserts a plain object as a new document.</summary>
    public CuteId Insert(CuteObject document) => Insert(new CuteDocument(document));

    /// <summary>Inserts a document parsed from JSON.</summary>
    public CuteId InsertJson(string json) => Insert(CuteDocument.Parse(json));

    /// <summary>
    /// Inserts many documents under a single lock and a single flush.
    /// </summary>
    /// <remarks>
    /// This is not a convenience wrapper around <see cref="Insert(CuteDocument)"/>: it takes the
    /// write lock once instead of once per document, and it leaves the log buffered until the end
    /// rather than flushing after each row. On a bulk load that is the difference between tens of
    /// thousands and millions of documents per second.
    /// </remarks>
    public int InsertMany(IEnumerable<CuteDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return _database.WriteBatch(this, documents, static (c, d) => c.UpsertCore(d, requireNew: true));
    }

    /// <summary>Replaces the document with the same id, or inserts it when there is none.</summary>
    public CuteId Save(CuteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _database.Write(this, document, static (c, d) => c.UpsertCore(d, requireNew: false));
    }

    /// <summary>Saves many documents under a single lock.</summary>
    public int SaveMany(IEnumerable<CuteDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return _database.WriteBatch(this, documents, static (c, d) => c.UpsertCore(d, requireNew: false));
    }

    /// <summary>Deletes a document by id. Returns false when it was not there.</summary>
    public bool Delete(CuteId id) => _database.Write(this, id, static (c, i) => c.DeleteCore(i));

    /// <summary>
    /// Deletes every document matching a CuteQL filter and returns how many were removed.
    /// </summary>
    public int DeleteWhere(string filter, CuteParameters? parameters = null)
    {
        var predicate = CuteParser.ParseExpression(filter);
        return _database.Write(this, (predicate, parameters), static (c, state) =>
        {
            var matches = c.FindRows(state.predicate, state.parameters, limit: int.MaxValue);
            var removed = 0;
            foreach (var row in matches)
            {
                if (c.DeleteCore(c._store.IdAt(row)))
                {
                    removed++;
                }
            }

            return removed;
        });
    }

    /// <summary>Removes every document, leaving indexes defined but empty.</summary>
    public void Clear() => _database.Write(this, 0, static (c, _) =>
    {
        c._database.AppendDropCollection(c.Id);
        c._store.Clear();
        foreach (var index in c._indexes.Values)
        {
            index.Clear();
        }

        return 0;
    });

    // ---------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------

    /// <summary>Fetches a document by id, or null when there is none.</summary>
    public CuteDocument? FindById(CuteId id) => _database.Read(this, id, static (c, i)
        => c._store.TryGetRow(i, out var row) ? CuteBinary.DecodeDocument(c._store.Read(row)) : null);

    /// <summary>True when a document with this id exists.</summary>
    public bool Exists(CuteId id) => _database.Read(this, id, static (c, i) => c._store.TryGetRow(i, out _));

    /// <summary>Every document in the collection, in row order.</summary>
    public IReadOnlyList<CuteDocument> All() => _database.Read(this, static c =>
    {
        var results = new List<CuteDocument>(c._store.Count);
        var refs = c._store.Refs;
        for (var row = 0; row < refs.Length; row++)
        {
            if (!refs[row].IsEmpty)
            {
                results.Add(CuteBinary.DecodeDocument(c._store.Read(row)));
            }
        }

        return (IReadOnlyList<CuteDocument>)results;
    });

    /// <summary>Finds documents matching a CuteQL filter expression.</summary>
    /// <example>
    /// <code>
    /// var jakarta = customers.Find("address.city = 'Jakarta' AND lifetimeValue > 1000000");
    /// </code>
    /// </example>
    public IReadOnlyList<CuteDocument> Find(string filter, CuteParameters? parameters = null, int limit = int.MaxValue)
    {
        var predicate = CuteParser.ParseExpression(filter);
        return _database.Read(this, (predicate, parameters, limit), static (c, state) =>
        {
            var rows = c.FindRows(state.predicate, state.parameters, state.limit);
            var results = new List<CuteDocument>(rows.Count);
            foreach (var row in rows)
            {
                results.Add(CuteBinary.DecodeDocument(c._store.Read(row)));
            }

            return (IReadOnlyList<CuteDocument>)results;
        });
    }

    /// <summary>Returns the first document matching a filter, or null.</summary>
    public CuteDocument? FindOne(string filter, CuteParameters? parameters = null)
        => Find(filter, parameters, limit: 1).FirstOrDefault();

    /// <summary>Counts the documents matching a filter.</summary>
    public int CountWhere(string filter, CuteParameters? parameters = null)
    {
        var predicate = CuteParser.ParseExpression(filter);
        return _database.Read(this, (predicate, parameters), static (c, state)
            => c.FindRows(state.predicate, state.parameters, int.MaxValue).Count);
    }

    /// <summary>Size and memory statistics for this collection.</summary>
    public CuteCollectionStats Stats() => _database.Read(this, static c => new CuteCollectionStats(
        c.Name,
        c._store.Count,
        c._indexes.Count,
        c._store.LiveBytes,
        c._store.DeadBytes,
        c._store.ReservedBytes));

    // ---------------------------------------------------------------------------------------
    // Indexes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a secondary index over a document path, building it from the documents already
    /// present.
    /// </summary>
    /// <param name="path">The path to index, such as <c>address.city</c> or <c>tags</c>.</param>
    /// <param name="name">An optional name; the path is used when omitted.</param>
    /// <param name="unique">Whether to reject duplicate keys.</param>
    public CuteIndexInfo CreateIndex(string path, string? name = null, bool unique = false)
    {
        var compiled = CutePath.Parse(path);
        var indexName = name ?? path;

        return _database.Write(this, (compiled, indexName, unique), static (c, state) =>
        {
            if (c._indexes.ContainsKey(state.indexName))
            {
                throw new CuteDbException($"Collection '{c.Name}' already has an index called '{state.indexName}'.");
            }

            var index = new SecondaryIndex(state.indexName, state.compiled, state.unique);
            index.Rebuild(c._store);
            c._indexes[state.indexName] = index;
            c._database.AppendDefineIndex(c.Id, state.indexName, state.compiled.Text, state.unique);
            return index.Info;
        });
    }

    /// <summary>Drops a secondary index. Returns false when there was none by that name.</summary>
    public bool DropIndex(string name) => _database.Write(this, name, static (c, n) =>
    {
        if (!c._indexes.Remove(n))
        {
            return false;
        }

        c._database.AppendDropIndex(c.Id, n);
        return true;
    });

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Count} documents)";

    // ---------------------------------------------------------------------------------------
    // Internals — all of these assume the caller already holds the appropriate database lock
    // ---------------------------------------------------------------------------------------

    internal CuteId UpsertCore(CuteDocument document, bool requireNew)
    {
        var id = document.Id;
        if (id.IsEmpty)
        {
            id = CuteId.NewId();
            document.Root.Set(CuteDocument.IdField, CuteValue.Id(id));
        }
        else if (requireNew && _store.TryGetRow(id, out _))
        {
            throw new CuteDbException(
                $"Collection '{Name}' already has a document with id {id}. Use Save to replace it.");
        }

        var writer = CuteBufferWriter.Rent();
        try
        {
            CuteBinary.Write(writer, CuteValue.Object(document.Root));
            var encoded = writer.WrittenSpan;

            ApplyIndexRemoval(id);
            var row = _store.Upsert(id, encoded, out _);
            ApplyIndexInsertion(row);

            _database.AppendUpsert(Id, id, encoded);
            return id;
        }
        finally
        {
            CuteBufferWriter.Return(writer);
        }
    }

    internal bool DeleteCore(CuteId id)
    {
        if (!_store.TryGetRow(id, out _))
        {
            return false;
        }

        ApplyIndexRemoval(id);
        _store.Delete(id);
        _database.AppendDelete(Id, id);
        return true;
    }

    /// <summary>Applies a replayed upsert during recovery, without writing back to the log.</summary>
    internal void ReplayUpsert(CuteId id, ReadOnlySpan<byte> document)
    {
        ApplyIndexRemoval(id);
        var row = _store.Upsert(id, document, out _);
        ApplyIndexInsertion(row);
    }

    /// <summary>Applies a replayed delete during recovery.</summary>
    internal void ReplayDelete(CuteId id)
    {
        ApplyIndexRemoval(id);
        _store.Delete(id);
    }

    /// <summary>Defines an index during recovery, without writing back to the log.</summary>
    internal void ReplayDefineIndex(string name, string path, bool unique)
    {
        var index = new SecondaryIndex(name, CutePath.Parse(path), unique);
        index.Rebuild(_store);
        _indexes[name] = index;
    }

    /// <summary>Drops an index during recovery.</summary>
    internal void ReplayDropIndex(string name) => _indexes.Remove(name);

    /// <summary>Clears the collection during recovery.</summary>
    internal void ReplayClear()
    {
        _store.Clear();
        foreach (var index in _indexes.Values)
        {
            index.Clear();
        }
    }

    /// <summary>Rebuilds every index from the current documents, after a bulk load.</summary>
    internal void RebuildIndexes()
    {
        foreach (var index in _indexes.Values)
        {
            index.Rebuild(_store);
        }
    }

    /// <summary>Reclaims space left by deleted and replaced documents.</summary>
    internal long CompactStore()
    {
        if (!_store.ShouldCompact)
        {
            return 0;
        }

        return _store.Compact();
    }

    /// <summary>Releases this collection's unmanaged memory.</summary>
    internal void Dispose() => _store.Dispose();

    /// <summary>
    /// Finds the rows matching a predicate, using an index when the planner can find a usable one
    /// and falling back to a scan otherwise.
    /// </summary>
    internal List<int> FindRows(CuteExpression? predicate, CuteParameters? parameters, int limit)
    {
        if (predicate is null)
        {
            var all = new List<int>(_store.Count);
            var refs = _store.Refs;
            for (var row = 0; row < refs.Length && all.Count < limit; row++)
            {
                if (!refs[row].IsEmpty)
                {
                    all.Add(row);
                }
            }

            return all;
        }

        return QueryPlanner.Execute(this, predicate, parameters, limit);
    }

    private void ApplyIndexInsertion(int row)
    {
        if (_indexes.Count == 0)
        {
            return;
        }

        var document = _store.Read(row);
        foreach (var index in _indexes.Values)
        {
            index.ExtractKeys(document, _keyScratch);
            foreach (var key in _keyScratch)
            {
                index.Add(key, row);
            }
        }
    }

    private void ApplyIndexRemoval(CuteId id)
    {
        if (_indexes.Count == 0 || !_store.TryGetRow(id, out var row))
        {
            return;
        }

        var document = _store.Read(row);
        foreach (var index in _indexes.Values)
        {
            index.ExtractKeys(document, _keyScratch);
            foreach (var key in _keyScratch)
            {
                index.Remove(key, row);
            }
        }
    }
}
