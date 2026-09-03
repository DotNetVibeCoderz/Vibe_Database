using System.Runtime.CompilerServices;

namespace CuteDB.Storage;

/// <summary>
/// The in-memory contents of one collection: a flat slot table of document ids and slab
/// references, plus the id lookup.
/// </summary>
/// <remarks>
/// <para>
/// Documents are addressed by <em>row</em>, a dense integer, rather than by id. The row is what
/// indexes point at, what a scan iterates, and what gets handed to the native accelerator; the id
/// dictionary is consulted only for a point lookup. Keeping the two apart means a scan touches
/// two contiguous arrays and never hashes anything.
/// </para>
/// <para>
/// Deleting leaves a hole: the slot's <see cref="DocRef"/> is cleared and the row goes on a free
/// list to be handed out again on the next insert. Scans skip holes with a length check, which
/// costs one comparison per row and avoids having to renumber rows — renumbering would invalidate
/// every index in the collection.
/// </para>
/// </remarks>
internal sealed class DocumentStore
{
    private const int InitialCapacity = 64;

    private readonly Dictionary<CuteId, int> _rowsById;
    private readonly List<int> _freeRows = [];

    private SlabAllocator _slabs;
    private DocRef[] _refs;
    private CuteId[] _ids;
    private int _highWater;

    internal DocumentStore(int slabSize = SlabAllocator.DefaultSlabSize)
    {
        _slabs = new SlabAllocator(slabSize);
        _refs = new DocRef[InitialCapacity];
        _ids = new CuteId[InitialCapacity];
        _rowsById = new Dictionary<CuteId, int>(InitialCapacity);
    }

    /// <summary>The number of live documents.</summary>
    internal int Count => _rowsById.Count;

    /// <summary>
    /// One past the highest row ever used. Scans run from 0 to here; some rows in between may be
    /// holes.
    /// </summary>
    internal int RowCount => _highWater;

    /// <summary>True when there are no holes, so a scan can skip its liveness check.</summary>
    internal bool IsDense => _freeRows.Count == 0 && _highWater == _rowsById.Count;

    /// <summary>Live document bytes held in slabs.</summary>
    internal long LiveBytes => _slabs.LiveBytes;

    /// <summary>Slab bytes reserved from the operating system.</summary>
    internal long ReservedBytes => _slabs.ReservedBytes;

    /// <summary>Bytes freed but not yet reclaimed.</summary>
    internal long DeadBytes => _slabs.DeadBytes;

    /// <summary>The slab allocator, for handing addresses to native code.</summary>
    internal SlabAllocator Slabs => _slabs;

    /// <summary>The slot table. Entries with a zero length are holes.</summary>
    internal ReadOnlySpan<DocRef> Refs => _refs.AsSpan(0, _highWater);

    /// <summary>The id of each row, parallel to <see cref="Refs"/>.</summary>
    internal ReadOnlySpan<CuteId> Ids => _ids.AsSpan(0, _highWater);

    /// <summary>Finds the row holding a document.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetRow(CuteId id, out int row) => _rowsById.TryGetValue(id, out row);

    /// <summary>True when the row is within range and not a hole.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsLive(int row) => (uint)row < (uint)_highWater && !_refs[row].IsEmpty;

    /// <summary>Reads the encoded document in a row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> Read(int row) => _slabs.Read(_refs[row]);

    /// <summary>The id in a row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CuteId IdAt(int row) => _ids[row];

    /// <summary>
    /// Inserts or replaces a document and reports which row it occupies and whether the id was
    /// already present.
    /// </summary>
    internal int Upsert(CuteId id, ReadOnlySpan<byte> encoded, out bool replaced)
    {
        if (_rowsById.TryGetValue(id, out var existing))
        {
            _slabs.Free(_refs[existing]);
            _refs[existing] = _slabs.Allocate(encoded);
            replaced = true;
            return existing;
        }

        replaced = false;
        var row = TakeRow();
        _ids[row] = id;
        _refs[row] = _slabs.Allocate(encoded);
        _rowsById[id] = row;
        return row;
    }

    /// <summary>Removes a document. Returns the row it occupied, or -1 when the id was unknown.</summary>
    internal int Delete(CuteId id)
    {
        if (!_rowsById.Remove(id, out var row))
        {
            return -1;
        }

        _slabs.Free(_refs[row]);
        _refs[row] = DocRef.None;
        _ids[row] = CuteId.Empty;
        _freeRows.Add(row);
        return row;
    }

    /// <summary>Drops every document, releasing all slab memory.</summary>
    internal void Clear()
    {
        _slabs.Dispose();
        _slabs = new SlabAllocator();
        _refs = new DocRef[InitialCapacity];
        _ids = new CuteId[InitialCapacity];
        _rowsById.Clear();
        _freeRows.Clear();
        _highWater = 0;
    }

    /// <summary>True once enough space is dead that <see cref="Compact"/> is worth running.</summary>
    internal bool ShouldCompact => _slabs.ShouldCompact;

    /// <summary>
    /// Rewrites every live document into fresh slabs, reclaiming the space left by deletes and
    /// updates. Rows keep their numbers, so indexes stay valid.
    /// </summary>
    internal long Compact()
    {
        var before = _slabs.ReservedBytes;
        var relocated = new DocRef[_highWater];
        var replacement = _slabs.CompactInto(_refs.AsSpan(0, _highWater), relocated);

        _slabs.Dispose();
        _slabs = replacement;
        relocated.AsSpan().CopyTo(_refs);
        return before - _slabs.ReservedBytes;
    }

    /// <summary>Releases the slabs.</summary>
    internal void Dispose() => _slabs.Dispose();

    private int TakeRow()
    {
        if (_freeRows.Count > 0)
        {
            var reused = _freeRows[^1];
            _freeRows.RemoveAt(_freeRows.Count - 1);
            return reused;
        }

        if (_highWater == _refs.Length)
        {
            var capacity = _refs.Length * 2;
            Array.Resize(ref _refs, capacity);
            Array.Resize(ref _ids, capacity);
        }

        return _highWater++;
    }
}
