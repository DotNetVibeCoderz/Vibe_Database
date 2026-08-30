using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Faiss.Net.Core;

namespace Faiss.Net.IO;

/// <summary>
/// A read-only flat index whose vectors stay in a memory-mapped file instead of the managed heap.
/// <para>
/// Nothing is copied at open time: the file is mapped into the address space and the search kernels
/// run directly against those pages. Three things follow. Opening a 40 GB index is instant and
/// costs no managed memory. Several processes mapping the same file share one set of physical
/// pages. And an index larger than RAM still works — the OS pages in what a query touches and
/// evicts the rest, which for a scan over a file read front to back is close to the ideal policy.
/// </para>
/// <para>
/// The cost is that a query touching cold pages waits on disk, so this is for indexes that are
/// large, read-mostly and searched in batches — not for latency-critical single lookups on a cold
/// cache. Write the file with the <c>Write</c> overload taking an <see cref="IndexFlat"/>, or from any index that can
/// reconstruct its vectors with the overload taking a general <see cref="Index"/>.
/// </para>
/// </summary>
public sealed unsafe class MappedIndexFlat : Index, IDisposable
{
    /// <summary>File magic: "FNMMAP01" in ASCII.</summary>
    private static readonly byte[] Magic = "FNMMAP01"u8.ToArray();

    /// <summary>Bytes before the vector data: magic, dimension, metric, count.</summary>
    private const int HeaderBytes = 8 + 4 + 4 + 8;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private byte* _basePointer;
    private float* _vectors;
    private bool _disposed;

    private MappedIndexFlat(int dimension, MetricType metric) : base(dimension, metric) { }

    /// <summary>
    /// Writes a mappable index file. The layout is a small header followed by raw little-endian
    /// float32 vectors, so the data region maps one-to-one onto the in-memory representation the
    /// search kernels expect — no parsing, no per-vector work at open time.
    /// </summary>
    public static void Write(IndexFlat index, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(index.D);
        writer.Write((int)index.MetricType);
        writer.Write(index.Ntotal);
        writer.Write(MemoryMarshal.AsBytes(index.Vectors));
    }

    /// <summary>
    /// Writes a mappable file from any index that supports reconstruction, decoding in batches so a
    /// compressed index can be converted without ever materializing the full float matrix.
    /// </summary>
    public static void Write(Index index, string path, int batchSize = 8192)
    {
        if (!index.SupportsReconstruct)
            throw new NotSupportedException($"{index.GetType().Name} cannot reconstruct vectors, so it cannot be mapped.");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(index.D);
        writer.Write((int)index.MetricType);
        writer.Write(index.Ntotal);

        var batch = new float[(long)batchSize * index.D];
        for (long start = 0; start < index.Ntotal; start += batchSize)
        {
            int count = (int)Math.Min(batchSize, index.Ntotal - start);
            index.ReconstructN(start, count, batch);
            writer.Write(MemoryMarshal.AsBytes(batch.AsSpan(0, count * index.D)));
        }
    }

    /// <summary>Maps an index file written by one of the <c>Write</c> overloads.</summary>
    public static MappedIndexFlat Open(string path)
    {
        int dimension;
        MetricType metric;
        long count;
        using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(header))
        {
            var magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Not a FAISS.Net memory-mapped index file.");
            dimension = reader.ReadInt32();
            metric = (MetricType)reader.ReadInt32();
            count = reader.ReadInt64();
        }

        var index = new MappedIndexFlat(dimension, metric);
        index._file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        index._view = index._file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        index._view.SafeMemoryMappedViewHandle.AcquirePointer(ref index._basePointer);
        index._vectors = (float*)(index._basePointer + HeaderBytes);
        index.Ntotal = count;
        index.IsTrained = true;
        return index;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Bytes of address space mapped (not resident memory, which the OS manages).</summary>
    public long MappedBytes => HeaderBytes + Ntotal * D * sizeof(float);

    /// <summary>A mapped index is read-only; rebuild and rewrite the file to change its contents.</summary>
    public override void Add(ReadOnlySpan<float> x) =>
        throw new NotSupportedException("MappedIndexFlat is read-only. Build an IndexFlat, then MappedIndexFlat.Write it.");

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nq == 0) return;
        if (Ntotal == 0)
        {
            distances.Fill(MetricType.IsSimilarity() ? float.MinValue : float.MaxValue);
            labels.Fill(-1);
            return;
        }

        fixed (float* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
            BruteForce.Knn(xq, nq, _vectors, (int)Ntotal, D, k, MetricType, pdis, plab, null, Threads);
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int nq = ValidateBatch(queries, nameof(queries));
        if (nq == 0 || Ntotal == 0) return new RangeSearchResult(new long[nq + 1], [], []);

        fixed (float* xq = queries)
            return BruteForce.RangeSearch(xq, nq, _vectors, (int)Ntotal, D, radius, MetricType, null, Threads);
    }

    public override void Reconstruct(long key, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (key < 0 || key >= Ntotal) throw new ArgumentOutOfRangeException(nameof(key));
        new ReadOnlySpan<float>(_vectors + key * D, D).CopyTo(output);
    }

    public override void Reset() =>
        throw new NotSupportedException("MappedIndexFlat is read-only.");

    /// <summary>Address space mapped. Resident memory is decided by the OS page cache.</summary>
    public override long MemoryUsage => MappedBytes;

    public override string Describe() =>
        $"MappedIndexFlat(d={D}, ntotal={Ntotal}, {MetricType.ToShortString()}, {MappedBytes / (1024 * 1024)}MB mapped)";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_basePointer != null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePointer = null;
            _vectors = null;
        }
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }
}
