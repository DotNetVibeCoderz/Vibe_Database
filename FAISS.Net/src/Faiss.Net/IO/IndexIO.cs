using Faiss.Net.Binary;

namespace Faiss.Net.IO;

/// <summary>
/// Binary persistence for indexes — the equivalent of <c>faiss.write_index</c> /
/// <c>faiss.read_index</c>.
/// <para>
/// The format is FAISS.Net's own, not FAISS's: it is little-endian, self-describing and versioned,
/// and it round-trips composite indexes by recursing through the same reader and writer used at the
/// top level, so an <c>IndexPreTransform(OPQ, IndexIVFPQ(quantizer: IndexFlatL2))</c> is written and
/// restored as a whole.
/// </para>
/// <para>
/// Every index writes a fixed header (type tag, dimension, metric, count, trained flag) followed by
/// a type-specific body. Because a type tag identifies the constructor to call, adding a new index
/// type never disturbs files written by older builds — see <see cref="IndexTypeCode"/> for the
/// append-only rule that keeps that guarantee.
/// </para>
/// </summary>
public static class IndexIO
{
    /// <summary>File magic: "FAISSNET" in ASCII.</summary>
    private static readonly byte[] Magic = "FAISSNET"u8.ToArray();

    /// <summary>Format version. Bumped only for a breaking layout change.</summary>
    public const int FormatVersion = 1;

    private static readonly Dictionary<IndexTypeCode, Func<int, MetricType, Index>> Factories = new()
    {
        [IndexTypeCode.Flat] = (d, m) => new IndexFlat(d, m),
        [IndexTypeCode.FlatL2] = (d, _) => new IndexFlatL2(d),
        [IndexTypeCode.FlatIP] = (d, _) => new IndexFlatIP(d),
        // IVF and compressed indexes are constructed in a minimal shape; their bodies carry the real
        // parameters and replace the placeholder quantizer, codebooks and lists wholesale.
        [IndexTypeCode.IVFFlat] = (d, m) => new IndexIVFFlat(new IndexFlatL2(d), d, 1, m),
        [IndexTypeCode.IVFPQ] = (d, m) => new IndexIVFPQ(new IndexFlatL2(d), d, 1, 1, 8, m),
        [IndexTypeCode.IVFScalarQuantizer] = (d, m) =>
            new IndexIVFScalarQuantizer(new IndexFlatL2(d), d, 1, ScalarQuantizerType.PerDimension8Bit, m),
        [IndexTypeCode.PQ] = (d, m) => new IndexPQ(d, 1, 8, m),
        [IndexTypeCode.ScalarQuantizer] = (d, m) => new IndexScalarQuantizer(d, ScalarQuantizerType.PerDimension8Bit, m),
        [IndexTypeCode.HNSWFlat] = (d, m) => new IndexHNSWFlat(d, 32, m),
        [IndexTypeCode.IDMap] = (d, m) => new IndexIDMap(new IndexFlat(d, m)),
        [IndexTypeCode.IDMap2] = (d, m) => new IndexIDMap2(new IndexFlat(d, m)),
        [IndexTypeCode.PreTransform] = IndexPreTransform.CreateForRead,
        [IndexTypeCode.Replicas] = (d, m) => new IndexReplicas(d, m),
        [IndexTypeCode.Shards] = (d, m) => new IndexShards(d, m),
    };

    private static readonly Dictionary<IndexTypeCode, Func<int, int, IndexBinary>> BinaryFactories = new()
    {
        [IndexTypeCode.BinaryFlat] = (d, _) => new IndexBinaryFlat(d),
        [IndexTypeCode.BinaryIVF] = (d, parameter) => new IndexBinaryIVF(d, Math.Max(1, parameter)),
    };

    // ------------------------------------------------------------- Public API

    /// <summary>Writes an index to a file. Equivalent to <c>faiss.write_index(index, path)</c>.</summary>
    public static void WriteIndex(Index index, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteIndex(index, stream);
    }

    /// <summary>Writes an index to a stream.</summary>
    public static void WriteIndex(Index index, Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        WriteTo(writer, index);
    }

    /// <summary>Reads an index from a file. Equivalent to <c>faiss.read_index(path)</c>.</summary>
    public static Index ReadIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return ReadIndex(stream);
    }

    /// <summary>Reads an index from a stream.</summary>
    public static Index ReadIndex(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        VerifyHeader(reader);
        return ReadFrom(reader);
    }

    /// <summary>Serializes an index to a byte array, for storing in a database or sending over a wire.</summary>
    public static byte[] Serialize(Index index)
    {
        using var stream = new MemoryStream();
        WriteIndex(index, stream);
        return stream.ToArray();
    }

    /// <summary>Restores an index produced by <see cref="Serialize(Index)"/>.</summary>
    public static Index Deserialize(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return ReadIndex(stream);
    }

    /// <summary>Writes a binary (Hamming) index.</summary>
    public static void WriteBinaryIndex(IndexBinary index, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((ushort)index.TypeCode);
        writer.Write(index.D);
        writer.Write(index.SerializationParameter);
        writer.Write(index.Ntotal);
        writer.Write(index.IsTrained);
        index.WriteBody(writer);
    }

    /// <summary>Reads a binary (Hamming) index.</summary>
    public static IndexBinary ReadBinaryIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        VerifyHeader(reader);

        var code = (IndexTypeCode)reader.ReadUInt16();
        int d = reader.ReadInt32();
        int parameter = reader.ReadInt32();
        reader.ReadInt64();   // ntotal, recomputed from the body
        reader.ReadBoolean(); // trained flag, recomputed from the body

        if (!BinaryFactories.TryGetValue(code, out var factory))
            throw new InvalidDataException($"Unknown binary index type code {code}.");

        var index = factory(d, parameter);
        index.ReadBody(reader);
        return index;
    }

    // ---------------------------------------------------------- Recursive IO

    /// <summary>
    /// Writes one index (header plus body) at the current position. Composite indexes call this for
    /// their children, which is what makes arbitrary nesting round-trip.
    /// </summary>
    public static void WriteTo(BinaryWriter writer, Index index)
    {
        writer.Write((ushort)index.TypeCode);
        writer.Write(index.D);
        writer.Write((int)index.MetricType);
        writer.Write(index.Ntotal);
        writer.Write(index.IsTrained);
        index.WriteBody(writer);
    }

    /// <summary>Reads one index written by <see cref="WriteTo"/>.</summary>
    public static Index ReadFrom(BinaryReader reader)
    {
        var code = (IndexTypeCode)reader.ReadUInt16();
        int d = reader.ReadInt32();
        var metric = (MetricType)reader.ReadInt32();
        long ntotal = reader.ReadInt64();
        bool isTrained = reader.ReadBoolean();

        if (!Factories.TryGetValue(code, out var factory))
            throw new InvalidDataException(
                $"Unknown index type code {code}. The file may come from a newer version of FAISS.Net.");

        var index = factory(d, metric);
        index.RestoreHeader(ntotal, isTrained);
        index.ReadBody(reader);
        return index;
    }

    private static void VerifyHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a FAISS.Net index file (bad magic).");

        int version = reader.ReadInt32();
        if (version > FormatVersion)
            throw new InvalidDataException(
                $"Index file format version {version} is newer than this build supports ({FormatVersion}).");
    }
}
