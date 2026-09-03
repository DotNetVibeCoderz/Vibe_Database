using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CuteDB.Storage;

/// <summary>
/// Where an encoded document lives inside a <see cref="SlabAllocator"/>: which slab, at what
/// offset, for how many bytes.
/// </summary>
/// <remarks>
/// The layout is fixed and matches <c>DocRef</c> in <c>native/cutedb-core/src/lib.rs</c> — an
/// array of these is handed to the accelerator as-is, so the three fields must stay in this order
/// and at this width.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct DocRef : IEquatable<DocRef>
{
    /// <summary>Index of the slab holding the document.</summary>
    public readonly uint Slab;

    /// <summary>Byte offset within that slab.</summary>
    public readonly uint Offset;

    /// <summary>Encoded length in bytes.</summary>
    public readonly uint Length;

    /// <summary>Creates a reference.</summary>
    public DocRef(uint slab, uint offset, uint length)
    {
        Slab = slab;
        Offset = offset;
        Length = length;
    }

    /// <summary>The reference used for an empty slot.</summary>
    public static DocRef None => default;

    /// <summary>True when this reference points at nothing.</summary>
    public bool IsEmpty => Length == 0;

    /// <inheritdoc />
    public bool Equals(DocRef other) => Slab == other.Slab && Offset == other.Offset && Length == other.Length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DocRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Slab, Offset, Length);

    /// <inheritdoc />
    public override string ToString() => $"slab {Slab} @{Offset} +{Length}";
}

/// <summary>
/// A bump allocator over large blocks of unmanaged memory, holding every encoded document in an
/// open database.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — one <c>byte[]</c> per document — is what most embedded stores do, and it is
/// what makes them fall over at scale: ten million documents become ten million live objects for
/// the GC to trace on every gen-2 collection, and each one carries a 24-byte object header on top
/// of its contents. Here, the same ten million documents are a few hundred slabs of unmanaged
/// memory the GC never looks at, and the per-document overhead is the twelve bytes of
/// <see cref="DocRef"/> in a flat array.
/// </para>
/// <para>
/// Allocation is a bump of a pointer. Freeing does not reclaim anything immediately; it just
/// records the dead bytes, and space is recovered in bulk by <see cref="CompactInto"/> once the
/// dead fraction crosses <see cref="CompactionThreshold"/>. That is the right trade for a store
/// where updates are common and deletes are rare, and it keeps the free path down to an addition.
/// </para>
/// <para>
/// Because the memory is unmanaged and never moves except during an explicit compaction, its
/// addresses can be handed straight to the Rust accelerator with no pinning and no copying.
/// </para>
/// </remarks>
public sealed unsafe class SlabAllocator : IDisposable
{
    /// <summary>Default size of one slab: 4 MiB.</summary>
    public const int DefaultSlabSize = 4 * 1024 * 1024;

    /// <summary>Dead fraction at which <see cref="ShouldCompact"/> starts returning true.</summary>
    public const double CompactionThreshold = 0.35;

    private readonly int _slabSize;
    private readonly List<nint> _slabs = [];
    private readonly List<int> _slabCapacities = [];

    private int _currentSlab = -1;
    private int _currentOffset;
    private long _liveBytes;
    private long _deadBytes;
    private nint[] _pointerCache = [];
    private bool _pointerCacheValid;
    private bool _disposed;

    /// <summary>Creates an allocator with a given slab size.</summary>
    public SlabAllocator(int slabSize = DefaultSlabSize)
    {
        if (slabSize < 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(slabSize), slabSize, "A slab must be at least 64 KiB.");
        }

        _slabSize = slabSize;
    }

    /// <summary>Bytes currently occupied by live documents.</summary>
    public long LiveBytes => _liveBytes;

    /// <summary>Bytes occupied by documents that have been freed but not yet reclaimed.</summary>
    public long DeadBytes => _deadBytes;

    /// <summary>Total unmanaged memory reserved across every slab.</summary>
    public long ReservedBytes
    {
        get
        {
            long total = 0;
            foreach (var capacity in _slabCapacities)
            {
                total += capacity;
            }

            return total;
        }
    }

    /// <summary>The number of slabs allocated.</summary>
    public int SlabCount => _slabs.Count;

    /// <summary>True once enough space is dead that a compaction would pay for itself.</summary>
    public bool ShouldCompact
        => _deadBytes > 8L * 1024 * 1024 && _deadBytes > (_liveBytes + _deadBytes) * CompactionThreshold;

    /// <summary>Copies a document into the allocator and returns where it landed.</summary>
    public DocRef Allocate(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.Length == 0)
        {
            throw new ArgumentException("A document cannot be empty.", nameof(data));
        }

        // Anything at least half a slab wide gets a slab of its own, sized exactly to fit. Letting
        // a 3 MiB document consume most of a shared slab would strand the remainder.
        if (data.Length >= _slabSize / 2)
        {
            var dedicated = AddSlab(data.Length);
            Copy(data, dedicated, 0);
            _liveBytes += data.Length;

            // The slab is full on creation, so the bump pointer stays on whatever slab it was on.
            return new DocRef((uint)dedicated, 0, (uint)data.Length);
        }

        if (_currentSlab < 0 || _currentOffset + data.Length > _slabCapacities[_currentSlab])
        {
            _currentSlab = AddSlab(_slabSize);
            _currentOffset = 0;
        }

        var reference = new DocRef((uint)_currentSlab, (uint)_currentOffset, (uint)data.Length);
        Copy(data, _currentSlab, _currentOffset);
        _currentOffset += data.Length;
        _liveBytes += data.Length;
        return reference;
    }

    /// <summary>Reads a document back. The span points into unmanaged memory and stays valid until the next compaction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Read(DocRef reference)
        => new((byte*)_slabs[(int)reference.Slab] + reference.Offset, (int)reference.Length);

    /// <summary>Marks a document's space as dead. The bytes are reclaimed at the next compaction.</summary>
    public void Free(DocRef reference)
    {
        if (reference.IsEmpty)
        {
            return;
        }

        _liveBytes -= reference.Length;
        _deadBytes += reference.Length;
    }

    /// <summary>
    /// The base address of every slab, in slab-index order, for handing to native code.
    /// </summary>
    /// <remarks>
    /// The array is cached and rebuilt only when a slab is added, because a scan asks for it once
    /// per call and slabs are added rarely.
    /// </remarks>
    public ReadOnlySpan<nint> SlabPointers
    {
        get
        {
            if (!_pointerCacheValid)
            {
                _pointerCache = [.. _slabs];
                _pointerCacheValid = true;
            }

            return _pointerCache;
        }
    }

    /// <summary>
    /// Copies every live document into a fresh allocator, in the order given, and reports where
    /// each one landed. The caller is responsible for swapping in the new allocator and updating
    /// its slot table from <paramref name="relocated"/>.
    /// </summary>
    public SlabAllocator CompactInto(ReadOnlySpan<DocRef> live, Span<DocRef> relocated)
    {
        if (relocated.Length < live.Length)
        {
            throw new ArgumentException("The relocation buffer is smaller than the input.", nameof(relocated));
        }

        var replacement = new SlabAllocator(_slabSize);
        try
        {
            for (var i = 0; i < live.Length; i++)
            {
                relocated[i] = live[i].IsEmpty ? DocRef.None : replacement.Allocate(Read(live[i]));
            }

            return replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases every slab. Slabs are unmanaged, so a forgotten allocator would leak until the
    /// process exits — hence the finaliser as a backstop.
    /// </summary>
    ~SlabAllocator() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);

        foreach (var slab in _slabs)
        {
            // Paired with AlignedAlloc: on Windows these come from _aligned_malloc, which the
            // plain free() does not understand.
            NativeMemory.AlignedFree((void*)slab);
        }

        _slabs.Clear();
        _slabCapacities.Clear();
        _pointerCache = [];
        _liveBytes = 0;
        _deadBytes = 0;
    }

    private int AddSlab(int capacity)
    {
        // Aligned to a cache line so that a document never straddles one unnecessarily and so the
        // native side can use aligned loads.
        var memory = NativeMemory.AlignedAlloc((nuint)capacity, 64);
        if (memory is null)
        {
            throw new OutOfMemoryException($"Could not reserve a {capacity} byte slab for CuteDB.");
        }

        _slabs.Add((nint)memory);
        _slabCapacities.Add(capacity);
        _pointerCacheValid = false;
        return _slabs.Count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Copy(ReadOnlySpan<byte> data, int slab, int offset)
        => data.CopyTo(new Span<byte>((byte*)_slabs[slab] + offset, data.Length));
}
