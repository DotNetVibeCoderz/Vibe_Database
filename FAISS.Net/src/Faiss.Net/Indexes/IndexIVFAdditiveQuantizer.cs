using System.Buffers;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Inverted file whose list entries are additive-quantizer codes — FAISS's
/// <c>IndexIVFAdditiveQuantizer</c>, the shared base of <see cref="IndexIVFResidualQuantizer"/> and
/// <see cref="IndexIVFLocalSearchQuantizer"/>.
/// <para>
/// The encoding choice mirrors <see cref="IndexIVFPQ"/>: for L2, vectors are encoded as residuals
/// from their cell centroid, which are far smaller than the vectors and so resolved more finely by
/// the same code; for inner product, raw vectors are encoded and the score is exact against the
/// decoded vector.
/// </para>
/// <para>
/// Unlike IVFPQ, residual coding costs nothing per probed cell. Each L2 code stores
/// <c>‖c + r̂‖² − ‖c‖²</c> (see <see cref="AdditiveQuantizer.ComputeCodes(ReadOnlySpan{float}, int, Span{byte}, ReadOnlySpan{float})"/>),
/// so the distance to the reconstruction <c>c + r̂</c> is the coarse distance <c>‖q − c‖²</c> the
/// quantizer already returned, minus <c>2⟨q, r̂⟩</c> from one lookup table built per query, plus the
/// stored term. The table never depends on the cell.
/// </para>
/// </summary>
public abstract class IndexIVFAdditiveQuantizer : IndexIVF
{
    /// <summary>The quantizer encoding list entries; exposed like <c>index.aq</c> in Python.</summary>
    public AdditiveQuantizer Aq { get; private set; }

    protected IndexIVFAdditiveQuantizer(Index quantizer, int dimension, int nlist, AdditiveQuantizer aq, MetricType metric)
        : base(quantizer, dimension, nlist, aq.CodeSize, metric)
    {
        if (metric is not (MetricType.L2 or MetricType.InnerProduct))
            throw new ArgumentException($"{GetType().Name} supports L2 and inner product, not {metric}.", nameof(metric));
        if (aq.D != dimension)
            throw new ArgumentException($"Quantizer dimension {aq.D} does not match index dimension {dimension}.");
        Aq = aq;
        ByResidual = metric == MetricType.L2;
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>, ignoring ids, counting the norm.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    protected override void TrainEncoder(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos)
    {
        if (!ByResidual)
        {
            Aq.Train(x[..(n * D)]);
            return;
        }
        var residuals = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals, null);
        Aq.Train(residuals);
    }

    /// <summary>Subtracts each vector's cell centroid, optionally keeping the centroid rows too.</summary>
    private void ComputeResiduals(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<float> residuals, float[]? centroids)
    {
        Span<float> centroid = D <= 1024 ? stackalloc float[D] : new float[D];
        int cached = -1;
        for (int i = 0; i < n; i++)
        {
            int list = (int)listNos[i];
            if (list < 0) list = 0;
            if (list != cached)
            {
                GetCentroid(list, centroid);
                cached = list;
            }
            var source = x.Slice(i * D, D);
            var target = residuals.Slice(i * D, D);
            for (int j = 0; j < D; j++) target[j] = source[j] - centroid[j];
            if (centroids is not null) centroid.CopyTo(centroids.AsSpan(i * D, D));
        }
    }

    protected override void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes)
    {
        if (!ByResidual)
        {
            Aq.ComputeCodes(x[..(n * D)], n, codes);
            return;
        }
        var residuals = new float[(long)n * D];
        var centroids = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals, centroids);
        Aq.ComputeCodes(residuals, n, codes, centroids);
    }

    /// <summary>The query's codeword inner-product table, shared by every probed list.</summary>
    protected override object? PrepareQuery(ReadOnlySpan<float> query)
    {
        var table = ArrayPool<float>.Shared.Rent(Aq.LookupTableSize);
        Aq.ComputeLookupTable(query, table);
        return table;
    }

    protected override void ReleaseQuery(object? queryState)
    {
        if (queryState is float[] table) ArrayPool<float>.Shared.Return(table);
    }

    protected override void ComputeListScores(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes, Span<float> scores,
        object? queryState)
    {
        var table = ((float[])queryState!).AsSpan(0, Aq.LookupTableSize);
        // L2 by residual: bias is ‖q − c‖²; the stored term carries ‖c + r̂‖² − ‖c‖².
        Aq.ScoreCodes(table, codes, scores, coarseScore, ByResidual);
    }

    protected override void DecodeEntry(int list, int offset, Span<float> output)
    {
        Aq.Decode(Lists.GetCodes(list).Slice(offset * CodeSize, CodeSize), output);
        if (!ByResidual) return;

        Span<float> centroid = D <= 1024 ? stackalloc float[D] : new float[D];
        GetCentroid(list, centroid);
        for (int j = 0; j < D; j++) output[j] += centroid[j];
    }

    public override long MemoryUsage => base.MemoryUsage + Aq.CodebookBytes;

    public override string Describe() =>
        $"{GetType().Name}(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {Aq}, " +
        $"{(ByResidual ? "residual" : "direct")}, {MetricType.ToShortString()})";

    // -------------------------------------------------------- Serialization

    /// <summary>Reads the concrete quantizer type this index holds.</summary>
    protected abstract AdditiveQuantizer ReadQuantizer(BinaryReader reader);

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(Nprobe);
        writer.Write(ByResidual);
        IndexIO.WriteTo(writer, Quantizer);
        Aq.Write(writer);
        Lists.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        Nprobe = reader.ReadInt32();
        ByResidual = reader.ReadBoolean();
        var quantizer = IndexIO.ReadFrom(reader);
        Aq = ReadQuantizer(reader);
        var lists = InvertedLists.Read(reader);
        RestoreIvf(quantizer, lists, nlist);
        IsTrained = Aq.IsTrained;
    }
}

/// <summary>
/// Inverted file over residual-quantizer codes — FAISS's
/// <c>faiss.IndexIVFResidualQuantizer(quantizer, d, nlist, M, nbits)</c>.
/// </summary>
public sealed class IndexIVFResidualQuantizer : IndexIVFAdditiveQuantizer
{
    /// <summary>The quantizer; exposed like <c>index.rq</c> in Python.</summary>
    public ResidualQuantizer Rq => (ResidualQuantizer)Aq;

    /// <param name="quantizer">Coarse quantizer, typically <c>new IndexFlatL2(d)</c>.</param>
    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells.</param>
    /// <param name="m">Number of stages.</param>
    /// <param name="nbits">Bits per stage; 8 is the default.</param>
    /// <param name="metric">L2 or inner product.</param>
    public IndexIVFResidualQuantizer(Index quantizer, int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(quantizer, dimension, nlist,
               new ResidualQuantizer(dimension, m, nbits, IndexAdditiveQuantizer.SearchTypeFor(metric)), metric)
    {
    }

    /// <summary>Convenience constructor that creates a matching flat coarse quantizer.</summary>
    public IndexIVFResidualQuantizer(int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : this(metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension),
               dimension, nlist, m, nbits, metric)
    {
    }

    protected override AdditiveQuantizer ReadQuantizer(BinaryReader reader) => ResidualQuantizer.Read(reader);

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IVFResidualQuantizer;
}

/// <summary>
/// Inverted file over local-search-quantizer codes — FAISS's
/// <c>faiss.IndexIVFLocalSearchQuantizer(quantizer, d, nlist, M, nbits)</c>.
/// </summary>
public sealed class IndexIVFLocalSearchQuantizer : IndexIVFAdditiveQuantizer
{
    /// <summary>The quantizer; exposed like <c>index.lsq</c> in Python.</summary>
    public LocalSearchQuantizer Lsq => (LocalSearchQuantizer)Aq;

    /// <param name="quantizer">Coarse quantizer, typically <c>new IndexFlatL2(d)</c>.</param>
    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells.</param>
    /// <param name="m">Number of codebooks.</param>
    /// <param name="nbits">Bits per codebook index; 8 is the default.</param>
    /// <param name="metric">L2 or inner product.</param>
    public IndexIVFLocalSearchQuantizer(Index quantizer, int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(quantizer, dimension, nlist,
               new LocalSearchQuantizer(dimension, m, nbits, IndexAdditiveQuantizer.SearchTypeFor(metric)), metric)
    {
    }

    /// <summary>Convenience constructor that creates a matching flat coarse quantizer.</summary>
    public IndexIVFLocalSearchQuantizer(int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : this(metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension),
               dimension, nlist, m, nbits, metric)
    {
    }

    protected override AdditiveQuantizer ReadQuantizer(BinaryReader reader) => LocalSearchQuantizer.Read(reader);

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IVFLocalSearchQuantizer;
}
